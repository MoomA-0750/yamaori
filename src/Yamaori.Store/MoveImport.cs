using Microsoft.Data.Sqlite;

namespace Yamaori.Store;

public sealed record MoveResult(int Visits, int Encounters, int Events, int Notes, int Worlds, int Favorites, int Albums, int AlbumPhotos, int Edits, int Trash, int Forgotten, int FriendAccounts);

/// <summary>
/// グループに入った端末が、それまで手元に持っていた記録をまとめ役へ移す（まとめ役で、その端末の DB の写しから足し込む）。
/// <list type="bullet">
/// <item>ログから読んだ訪問はふだんの送り方（/api/sync/projection）で届くので、ここでは取り込んだ訪問（vat:…・vrcx:… など、log: 以外）だけ</item>
/// <item>アルバムは同じ名前があればそこへ足す。写真の直しは新しい方を残す。ゴミ箱・お気に入り・メモ・ワールドの情報は、まとめ役にないものだけ足す</item>
/// <item>忘れた人は、まとめ役でも忘れる（足し込む前に）。フレンドの写しは、アカウントごとに新しい方</item>
/// <item>まとめ役にあるものは消さない。同じ写しを何度送っても、二重にはならない</item>
/// </list>
/// 写しは同じ表の版のときだけ受け付ける（版が違えば、先に同じ版にしてもらう）。
/// </summary>
public sealed class MoveImport(Database db, string deviceId)
{
    public MoveResult Import(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("写しが見つからない", file);
        int version;
        using (var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check";
            if (cmd.ExecuteScalar() as string != "ok") throw new InvalidDataException("写しの DB が壊れている");
            cmd.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
            cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('visits', 'albums', 'photo_edits', 'group_members')";
            if (Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 4) throw new InvalidDataException("Yamaori の DB ではない");
        }
        if (version != Database.SchemaVersion)
            throw new InvalidDataException($"写しの表の版（{version}）が、まとめ役（{Database.SchemaVersion}）と違う。両方を同じ版の Yamaori にしてから移す");

        // 忘れた人を先に忘れる（このあと足し込む会った人・出来事・メモは、トリガーで落ちる）
        var forgotten = 0;
        var forget = new ForgetStore(db);
        foreach (var (key, label) in ReadForgotten(file))
        {
            if (db.Scalar("SELECT 1 FROM forgotten_people WHERE key = $k", ("$k", key)) != null) continue;
            try { forget.Forget(key, label); forgotten++; }
            catch (ArgumentException) { }
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
            const string Imported = "source NOT LIKE 'log:%'";
            // 忘れた人の行は足さない（トリガーでも落ちるが、数に入れないため）
            const string Remembered = "NOT (coalesce(user_id, '') IN (SELECT key FROM main.forgotten_people) OR (user_id IS NULL AND 'name:' || name IN (SELECT key FROM main.forgotten_people)))";
            var visits = Run($"INSERT OR IGNORE INTO main.visits SELECT * FROM mv.visits WHERE {Imported}");
            Run($"UPDATE main.visits SET device_id = $d WHERE device_id IS NULL AND id IN (SELECT id FROM mv.visits WHERE {Imported})", ("$d", deviceId));
            var encounters = Run($"INSERT OR IGNORE INTO main.encounters SELECT * FROM mv.encounters WHERE {Imported} AND {Remembered}");
            Run($"UPDATE main.encounters SET device_id = $d WHERE device_id IS NULL AND id IN (SELECT id FROM mv.encounters WHERE {Imported})", ("$d", deviceId));
            var events = Run($"INSERT OR IGNORE INTO main.events SELECT * FROM mv.events WHERE {Imported} AND {Remembered}");
            Run($"UPDATE main.events SET device_id = $d WHERE device_id IS NULL AND id IN (SELECT id FROM mv.events WHERE {Imported})", ("$d", deviceId));

            // メモは、まとめ役で消したもの（墓石より古い版）は戻さない
            var notes = Run("""
                INSERT OR IGNORE INTO main.notes SELECT * FROM mv.notes n
                WHERE NOT EXISTS (SELECT 1 FROM main.note_deletions d WHERE d.id = n.id AND d.deleted_at >= n.updated_at)
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
            using (var cmd = db.Command("SELECT id, name FROM mv.albums", tx))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) map[r.GetString(0)] = r.GetString(1);
            foreach (var (id, name) in map.ToList())
            {
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

            // 写真の直しは、新しく直した方を残す
            var edits = Run($"""
                INSERT INTO main.photo_edits SELECT * FROM mv.photo_edits WHERE true
                ON CONFLICT (photo_id) DO UPDATE SET {string.Join(", ", editCols.Select(c => $"{c} = excluded.{c}"))}
                WHERE excluded.edited_at > main.photo_edits.edited_at
                """);
            var trash = Run("INSERT OR IGNORE INTO main.photo_trash SELECT * FROM mv.photo_trash");

            // フレンドの写しは、アカウントごとに新しく撮った方
            var accounts = new List<string>();
            using (var cmd = db.Command("""
                SELECT s.account_id FROM mv.friend_snapshot_meta s
                LEFT JOIN main.friend_snapshot_meta m ON m.account_id = s.account_id
                WHERE m.account_id IS NULL OR s.taken_at > m.taken_at
                """, tx))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) accounts.Add(r.GetString(0));
            foreach (var a in accounts)
            {
                Run("DELETE FROM main.friend_snapshots WHERE account_id = $a", ("$a", a));
                Run("INSERT INTO main.friend_snapshots SELECT * FROM mv.friend_snapshots WHERE account_id = $a", ("$a", a));
                Run("INSERT OR REPLACE INTO main.friend_snapshot_meta SELECT * FROM mv.friend_snapshot_meta WHERE account_id = $a", ("$a", a));
            }
            tx.Commit();
            return new MoveResult(visits, encounters, events, notes, worlds, favorites, albums, albumPhotos, edits, trash, forgotten, accounts.Count);
        }
        finally
        {
            Exec("DETACH DATABASE mv");
        }
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

    static List<(string Key, string? Label)> ReadForgotten(string file)
    {
        var list = new List<(string, string?)>();
        using var src = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        src.Open();
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT key, label FROM forgotten_people";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
        return list;
    }
}
