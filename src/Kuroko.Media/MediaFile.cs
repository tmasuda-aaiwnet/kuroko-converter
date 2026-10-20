using System.IO;

namespace Kuroko.Media.Dvd;

// ディスクのイメージ（ISO）・krk を読むときの「ファイルを開く入口」（docs/file-source-spec.md の第 2 段）。
// DvdCore は Core（FileSources）を参照しないので、差し込み口を置く。ファサードは「OS のファイルでないキー
//（Apple の smb:// 等）ならファイル源から開く」を差し込む。差し込みが無いか null を返したら、従来どおり FileStream。
// Windows のアプリは差し込まない（動きは変わらない）。
internal static class MediaFile
{
    // パス（キー）→ Stream。null を返したら FileStream で開く。krk は Kuroko SDK が自分で開くので、同じ差し込みを
    // SDK の KurokoIo にも渡す（ファサードは MediaFile だけを設定すればよい）。
    private static Func<string, Stream?>? s_override;
    public static Func<string, Stream?>? OpenReadOverride
    {
        get => s_override;
        set { s_override = value; KurokoIo.OpenReadOverride = value; }
    }

    // 書き込み中のファイル（録画中の TS）も開けるよう、他のプロセスの書き込み・削除を許す（FileShare.Read だけだと、録画ソフトが
    // 書き込みで開いている間は「使用中」で開けない。Vuecove の中身の判定がこれで失敗し、再生（ReadWrite で開く）はできていた）。
    public static Stream OpenRead(string path, int bufferSize = 4096)
        => OpenReadOverride?.Invoke(path)
           ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize);
}
