# パッケージのフォルダを zip か tar.gz にまとめる（build-release.sh から呼ぶ）。
# tar.gz は、Windows で作っても Linux で実行できるよう、実行ファイルに実行の権限（755）を付けて書く。
# Packs a staged package folder into .zip or .tar.gz (called from build-release.sh). The tar.gz gets mode 755 on the
# executables even when built on Windows, so that it runs on Linux as extracted.
import os, sys, tarfile, zipfile

src, out = sys.argv[1], sys.argv[2]
executables = set(sys.argv[3:])          # パッケージの中の実行ファイルの名前 / executable file names in the package
base = os.path.dirname(os.path.abspath(src))
name = os.path.basename(os.path.abspath(src))

if out.endswith('.zip'):
    with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED) as z:
        for dp, _, fs in os.walk(src):
            for f in sorted(fs):
                p = os.path.join(dp, f)
                z.write(p, os.path.relpath(p, base))
else:
    def norm(ti):
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = 'root'
        if ti.isdir():
            ti.mode = 0o755
        elif os.path.basename(ti.name) in executables:
            ti.mode = 0o755
        else:
            ti.mode = 0o644
        return ti
    with tarfile.open(out, 'w:gz') as t:
        t.add(src, arcname=name, filter=norm)
print(out)
