#!/usr/bin/env python3
"""Yamaori の写真の縮小を、写真のある機械（NAS など）の上で作る。

写真のフォルダ（root）の下の写真を新しい順に読み、root/.yamaori/thumbs/<ID の頭 4 文字>/<ID>_<大きさ>.jpg を作る。
ID は Yamaori と同じ決め方（"photo\\n<ファイル名>\\n<バイト数>" の SHA-256 の頭 10 バイト。Yamaori が XMP に書いた yamaori:PhotoId があればそれ）。
Yamaori はこの縮小を見つけると、写真を読まずにそれを使う。

要るもの: Python 3 と Pillow（TrueNAS SCALE には最初から入っている）。
使い方: python3 yamaori-thumbs.py --root /mnt/pool/photos/VRChat [--sizes 320] [--jobs 2]
進み具合は root/.yamaori/thumbs-status.json に書く。同じ root で 2 つ同時には動かない。
"""
import argparse, hashlib, json, os, re, sys, time

YAMAORI_ID = re.compile(rb"<yamaori:PhotoId>(p_[0-9a-f]{20})</yamaori:PhotoId>")

EXTS = (".png", ".jpg", ".jpeg", ".webp")
SKIP_DIR = ".yamaori"


def photo_id(name, size, path=None):
    # Yamaori が情報を書いた写真は、書いた ID を使う（書いて大きさが変わっても同じ写真）
    if path is not None:
        try:
            with open(path, "rb") as f:
                head = f.read(1 << 20)
            m = YAMAORI_ID.search(head)
            if m:
                return m.group(1).decode("ascii")
        except OSError:
            pass
    key = "photo\n%s\n%d" % (name, size)
    return "p_" + hashlib.sha256(key.encode("utf-8")).hexdigest()[:20]


def out_path(root, pid, size):
    return os.path.join(root, SKIP_DIR, "thumbs", pid[:4], "%s_%d.jpg" % (pid, size))


def make(args):
    path, root, sizes = args
    from PIL import Image
    try:
        st = os.stat(path)
        pid = photo_id(os.path.basename(path), st.st_size, path)
        todo = [s for s in sizes if not os.path.exists(out_path(root, pid, s))]
        if not todo:
            return "skipped"
        with Image.open(path) as im:
            if im.format == "JPEG":
                im.draft("RGB", (max(todo), max(todo)))
            im.load()
            for s in sorted(todo, reverse=True):
                scale = min(1.0, float(s) / max(im.width, im.height))
                w, h = max(1, round(im.width * scale)), max(1, round(im.height * scale))
                small = im.convert("RGB").resize((w, h), Image.Resampling.LANCZOS) if (w, h) != im.size else im.convert("RGB")
                dest = out_path(root, pid, s)
                os.makedirs(os.path.dirname(dest), exist_ok=True)
                tmp = dest + ".tmp"
                small.save(tmp, "JPEG", quality=82)
                os.replace(tmp, dest)
        return "made"
    except Exception as e:  # 読めない写真は飛ばして続ける
        return "failed: %s: %s" % (path, e)


def photos(root):
    found = []
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d != SKIP_DIR]
        for f in filenames:
            if f.lower().endswith(EXTS):
                found.append(os.path.join(dirpath, f))
    # ファイル名に撮った日時が入っているので、名前の逆順でおおよそ新しい順になる
    found.sort(key=lambda p: os.path.basename(p), reverse=True)
    return found


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--root", required=True)
    ap.add_argument("--sizes", default="320")
    ap.add_argument("--jobs", type=int, default=2)
    a = ap.parse_args()
    root = os.path.abspath(a.root)
    sizes = [int(x) for x in a.sizes.split(",") if x.strip()]
    base = os.path.join(root, SKIP_DIR)
    os.makedirs(base, exist_ok=True)
    lock = os.path.join(base, "thumbs.lock")
    status_path = os.path.join(base, "thumbs-status.json")
    try:
        fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
        os.write(fd, str(os.getpid()).encode())
        os.close(fd)
    except FileExistsError:
        try:
            pid = int(open(lock).read().strip() or "0")
            os.kill(pid, 0)
            print("もう動いている（pid %d）" % pid)
            return 0
        except (ValueError, ProcessLookupError, PermissionError):
            os.replace(lock, lock + ".stale")
            return main()
    status = {"running": True, "started": time.time(), "updated": time.time(), "total": 0, "made": 0, "skipped": 0, "failed": 0, "errors": []}

    def write_status():
        status["updated"] = time.time()
        tmp = status_path + ".tmp"
        with open(tmp, "w") as f:
            json.dump(status, f)
        os.replace(tmp, status_path)

    try:
        try:
            os.nice(19)
        except OSError:
            pass
        files = photos(root)
        status["total"] = len(files)
        write_status()
        from multiprocessing import Pool
        with Pool(max(1, a.jobs)) as pool:
            for i, r in enumerate(pool.imap(make, ((p, root, sizes) for p in files), chunksize=4)):
                if r == "made":
                    status["made"] += 1
                elif r == "skipped":
                    status["skipped"] += 1
                else:
                    status["failed"] += 1
                    if len(status["errors"]) < 20:
                        status["errors"].append(r[:300])
                if i % 20 == 0:
                    write_status()
    finally:
        status["running"] = False
        write_status()
        try:
            os.remove(lock)
        except OSError:
            pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
