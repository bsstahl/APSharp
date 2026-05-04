namespace Fedi;

public class Upload
{
    public byte[] Buffer { get; set; }
    public string MediaType { get; set; }
    public string Relative { get; set; }
    public string? ObjectId { get; set; }

    private static string GetExtension(string mediaType)
    {
        return mediaType.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpg",
            "image/png" => "png",
            "image/gif" => "gif",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            "text/html" => "html",
            "text/plain" => "txt",
            "application/json" => "json",
            "application/pdf" => "pdf",
            "application/xml" => "xml",
            "application/octet-stream" => "bin",
            _ => "bin"
        };
    }

    public Upload(byte[] buffer, string mediaType)
    {
        Buffer = buffer;
        MediaType = mediaType;
        var extension = GetExtension(mediaType);
        Relative = $"{Nanoid.Generate()}.{extension}";
    }

    private Upload() {
        Buffer = Array.Empty<byte>();
        MediaType = "application/octet-stream";
        Relative = "";
    }

    public static async Task<Upload?> FromRelative(string relative, Database db)
    {
        var row = await db.GetRowAsync("SELECT * FROM upload_2 WHERE relative = ?", relative);
        if (row == null) return null;
        return new Upload
        {
            Relative = row["relative"]?.ToString() ?? "",
            MediaType = row["mediaType"]?.ToString() ?? "",
            ObjectId = row["objectId"] as string
        };
    }

    public string FilePath() => Path.Combine(AppState.UploadDir ?? Path.GetTempPath(), Relative);

    public async Task<bool> Readable()
    {
        try
        {
            var fi = new FileInfo(FilePath());
            return fi.Exists;
        }
        catch
        {
            return false;
        }
    }

    public async Task Save(Database db)
    {
        if (ObjectId == null) throw new Exception("Cannot save upload without objectId");
        await File.WriteAllBytesAsync(FilePath(), Buffer);
        await db.RunAsync("INSERT INTO upload_2 (relative, mediaType, objectId) VALUES (?, ?, ?)", Relative, MediaType, ObjectId);
    }
}
