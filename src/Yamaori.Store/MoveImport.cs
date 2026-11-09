using Microsoft.Data.Sqlite;

namespace Yamaori.Store;

public sealed record MoveResult(int Visits, int Encounters, int Events, int Notes, int Worlds, int Favorites, int Albums, int AlbumPhotos, int Edits, int Trash, int Forgotten, int FriendAccounts);

/// <summary>
/// グループに入った端末が、それまで手元に持っていた記録をまとめ役へ移す（まとめ役で、その端末の DB の写しから足し込む）。
/// <list type="bullet">
/// <item>ログから読んだ訪問はふだんの送り方（/api/sync/projection）で届くので、ここでは取り込んだ訪問（vat:…・vrcx:… など、log: 以外）だけ</item>
/// <item>アルバムは同じ名前があればそこへ足す。写真の直しは新しい方を残す。ゴミ箱・お気に入り・メモ・ワールドの情報は、まとめ役にないものだけ足す</item>
/// <item>忘れた人は、まとめ役でも忘れる。フレンドの写しは、アカウントごとに新しい方</item>
/// <item>まとめ役にあるものは消さない。全部を一つのトランザクションで行い、途中で失敗したら何も変えない</item>
/// <item>端末ごとに一度だけ（済んだあとにまとめ役で消したアルバムなどを、古い写しで戻さないため）。グループから外すと、もう一度受け付ける</item>
/// </list>
/// 写しは同じ表の版で、使う表の形（実表であること・列の名前と順・型・NOT NULL・主キー）がまとめ役と同じときだけ受け付ける。
/// </summary>
public sealed class MoveImport(Database db, string deviceId)
{
    public static string DoneKey(string deviceId) => "moved:" + deviceId;

    /// <summary>足し込むのに使う表。全部、まとめ役と同じ形でなければ断る。</summary>
    static readonly string[] Tables =
        ["visits", "encounters", "events", "notes", "worlds", "world_favorites", "albums", "album_photos", "photo_edits", "photo_trash",
         "friend_snapshots", "friend_snapshot_meta", "forgotten_people"];

    /// <summary>時刻として読める、この先 1 日までの UTC の文字列か（でたらめな値を「いちばん新しい」にさせない）。</summary>
    static string ValidTime(string col) =>
        $"({col} GLOB '[12][0-9][0-9][0-9]-[01][0-9]-[0-3][0-9]T[0-2][0-9]:[0-5][0-9]:[0-5][0-9]*Z' AND julianday({col}) IS NOT NULL AND julianday({col}) <= julianday('now', '+1 day'))";

    public MoveResult Import(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("写しが見つからない", file);
        if (new AuthStore(db).Get(DoneKey(deviceId)) is { } done)
            throw new InvalidOperationException($"この端末の記録は {done} に移し済み（もう一度移すときは、まとめ役の画面でこの端末をグループから外し、入り直してから）");
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandText = "PRAGMA trusted_schema = OFF";
            cmd.ExecuteNonQuery();
            cmd.CommandText = "PRAGMA quick_check";
            if (cmd.ExecuteScalar() as string != "ok") throw new InvalidDataException("写しの DB が壊れている");
            cmd.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            if (version != Database.SchemaVersion)
                throw new InvalidDataException($"写しの表の版（{version}）が、まとめ役（{Database.SchemaVersion}）と違う。両方を同じ版の Yamaori にしてから移す");
            foreach (var t in Tables)
            {
                cmd.CommandText = "SELECT type FROM sqlite_schema WHERE name = $n";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("$n", t);
                if (cmd.ExecuteScalar() as string != "table") throw new InvalidDataException($"Yamaori の DB ではない（{t} が表でない）");
                cmd.Parameters.Clear();
                if (Shape(src, t) != Shape(db.Connection, t)) throw new InvalidDataException($"写しの表 {t} の形が、まとめ役と違う");
            }
        }

        Exec("PRAGMA trusted_schema = OFF");
        using (var attach = db.Command("ATTACH DATABASE $f AS mv", null, ("$f", file))) attach.ExecuteNonQuery();
        try
        {
            var worldCols = Columns("worlds").Where(c => c != "id").ToList();
            var editCols = Columns("photo_edits").Where(c => c != "photo_id").ToList();
            using var tx = db.Connection.BeginTransaction();
            int Run(string sql, params (string, object?)[] args)
            {
                using var cmd = db.Command(sql, tx, args);
                return cmd.ExecuteNonQuery();
            }
            object? One(string sql, params (string, object?)[] args)
            {
                using var cmd = db.Command(sql, tx, args);
                return cmd.ExecuteScalar();
            }
            List<(string, string?)> Pairs(string sql)
            {
                var list = new List<(string, string?)>();
                using var cmd = db.Command(sql, tx);
                using var r = cmd.ExecuteReader();
                while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
                return list;
            }

            // 忘れた人を先に忘れる（このあと足し込む会った人・出来事・メモは、トリガーでも落ちる）。同じトランザクションの中で
            var forgotten = 0;
            var forget = new ForgetStore(db);
            foreach (var (key, label) in Pairs("SELECT key, label FROM mv.forgotten_people WHERE key NOT IN (SELECT key FROM main.forgotten_people)"))
            {
                if (!(key.StartsWith("usr_", StringComparison.Ordinal) || key.StartsWith("name:", StringComparison.Ordinal)) || key.Length > 300) continue;
                forget.Forget(key, label, tx);
                forgotten++;
            }

            const string Imported = "source NOT LIKE 'log:%'";
            // 忘れた人の行は足さない（トリガーでも落ちるが、数に入れないため）
            const string Remembered = "NOT (coalesce(user_id, '') IN (SELECT key FROM main.forgotten_people) OR (user_id IS NULL AND 'name:' || name IN (SELECT key FROM main.forgotten_people)))";
            // 新しく入る行にだけ、この端末を付ける（まとめ役に元からある同じ ID の行は変えない）
            int Insert(string table, string where)
            {
                Run("DROP TABLE IF EXISTS temp.mv_new");
                Run($"CREATE TEMP TABLE mv_new AS SELECT id FROM mv.{table} WHERE {where} AND id NOT IN (SELECT id FROM main.{table})");
                var n = Run($"INSERT INTO main.{table} SELECT * FROM mv.{table} WHERE id IN (SELECT id FROM temp.mv_new)");
                Run($"UPDATE main.{table} SET device_id = $d WHERE device_id IS NULL AND id IN (SELECT id FROM temp.mv_new)", ("$d", deviceId));
                Run("DROP TABLE temp.mv_new");
                return n;
            }
            var visits = Insert("visits", Imported);
            var encounters = Insert("encounters", $"{Imported} AND {Remembered}");
            var events = Insert("events", $"{Imported} AND {Remembered}");

            // メモは、まとめ役で消したもの（墓石より古い版）は戻さない
            var notes = Run($"""
                INSERT OR IGNORE INTO main.notes SELECT * FROM mv.notes n
                WHERE {ValidTime("n.updated_at")}
                  AND NOT EXISTS (SELECT 1 FROM main.note_deletions d WHERE d.id = n.id AND d.deleted_at >= n.updated_at)
                """);

            // ワールドの情報は、まとめ役にない行を足し、ある行は空いているところだけ埋める
            var worlds = Run("INSERT OR IGNORE INTO main.worlds SELECT * FROM mv.worlds");
            Run($"""
                UPDATE main.worlds SET {string.Join(", ", worldCols.Select(c => $"{c} = coalesce({c}, (SELECT w.{c} FROM mv.worlds w WHERE w.id = main.worlds.id))"))}
                WHERE id IN (SELECT id FROM mv.worlds)
                """);
            var favorites = Run("INSERT OR IGNORE INTO main.world_favorites SELECT * FROM mv.world_favorites");

            // アルバム: 同じ名前があればそこへ、なければ同じ ID で作る
            var albums = 0;
            var map = new Dictionary<string, string>();
            foreach (var (id, name) in Pairs("SELECT id, name FROM mv.albums"))
            {
                if (name == null) continue;
                if (One("SELECT id FROM main.albums WHERE name = $n ORDER BY created_at LIMIT 1", ("$n", name)) is string existing)
                {
                    map[id] = existing;
                    continue;
                }
                var target = One("SELECT 1 FROM main.albums WHERE id = $i", ("$i", id)) == null ? id : "a_" + Guid.NewGuid().ToString("N")[..20];
                Run("INSERT INTO main.albums (id, name, description, cover_photo_id, created_at, updated_at) SELECT $t, name, description, cover_photo_id, created_at, updated_at FROM mv.albums WHERE id = $i",
                    ("$t", target), ("$i", id));
                map[id] = target;
                albums++;
            }
            var albumPhotos = 0;
            foreach (var (from, to) in map)
            {
                albumPhotos += Run("INSERT OR IGNORE INTO main.album_photos (album_id, photo_id, added_at) SELECT $to, photo_id, added_at FROM mv.album_photos WHERE album_id = $from",
                    ("$to", to), ("$from", from));
                Run("UPDATE main.albums SET cover_photo_id = (SELECT cover_photo_id FROM mv.albums WHERE id = $from) WHERE id = $to AND cover_photo_id IS NULL", ("$to", to), ("$from", from));
            }

            // 写真の直しは、新しく直した方を残す（時刻として読めないものは入れない）
            var edits = Run($"""
                INSERT INTO main.photo_edits SELECT * FROM mv.photo_edits WHERE {ValidTime("edited_at")}
                ON CONFLICT (photo_id) DO UPDATE SET {string.Join(", ", editCols.Select(c => $"{c} = excluded.{c}"))}
                WHERE julianday(excluded.edited_at) > julianday(main.photo_edits.edited_at)
                """);
            var trash = Run("INSERT OR IGNORE INTO main.photo_trash SELECT * FROM mv.photo_trash");

            // フレンドの写しは、アカウントごとに新しく撮った方
            var accounts = Pairs($"""
                SELECT s.account_id, NULL FROM mv.friend_snapshot_meta s
                LEFT JOIN main.friend_snapshot_meta m ON m.account_id = s.account_id
                WHERE {ValidTime("s.taken_at")} AND (m.account_id IS NULL OR julianday(s.taken_at) > julianday(m.taken_at))
                """).Select(x => x.Item1).ToList();
            foreach (var a in accounts)
            {
                Run("DELETE FROM main.friend_snapshots WHERE account_id = $a", ("$a", a));
                Run("INSERT INTO main.friend_snapshots SELECT * FROM mv.friend_snapshots WHERE account_id = $a", ("$a", a));
                Run("INSERT OR REPLACE INTO main.friend_snapshot_meta SELECT * FROM mv.friend_snapshot_meta WHERE account_id = $a", ("$a", a));
            }
            Run("INSERT INTO main.settings (key, value) VALUES ($k, $v) ON CONFLICT (key) DO UPDATE SET value = $v",
                ("$k", DoneKey(deviceId)), ("$v", Database.Time(DateTime.UtcNow)));
            tx.Commit();
            return new MoveResult(visits, encounters, events, notes, worlds, favorites, albums, albumPhotos, edits, trash, forgotten, accounts.Count);
        }
        finally
        {
            Exec("DROP TABLE IF EXISTS temp.mv_new");
            Exec("DETACH DATABASE mv");
        }
    }

    /// <summary>表の形（列の名前・順・型・NOT NULL・主キーの位置）。</summary>
    static string Shape(SqliteConnection c, string table)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT name, type, \"notnull\", pk, hidden FROM pragma_table_xinfo('{table}', 'main') ORDER BY cid";
        using var r = cmd.ExecuteReader();
        var parts = new List<string>();
        while (r.Read()) parts.Add($"{r.GetString(0)}:{r.GetString(1).ToUpperInvariant()}:{r.GetInt64(2)}:{r.GetInt64(3)}:{r.GetInt64(4)}");
        return string.Join("|", parts);
    }

    void Exec(string sql)
    {
        using var cmd = db.Command(sql);
        cmd.ExecuteNonQuery();
    }

    List<string> Columns(string table)
    {
        var cols = new List<string>();
        using var cmd = db.Command($"SELECT name FROM pragma_table_info('{table}')");
        using var r = cmd.ExecuteReader();
        while (r.Read()) cols.Add(r.GetString(0));
        return cols;
    }
}
