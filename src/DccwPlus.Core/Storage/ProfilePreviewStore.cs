using System.Text.Json;
using System.Text.Json.Serialization;
using DccwPlus.Core.Calibration;

namespace DccwPlus.Core.Storage;

public sealed class ProfilePreviewStore(string root) : IProfilePreviewJournal
{
    private readonly string _root = Path.Combine(Path.GetFullPath(root), "profile-previews");
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, MaxDepth = 20, AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public string DirectoryFor(Guid id) => Path.Combine(_root, id.ToString("N"));
    public void Save(ProfilePreviewSession session)
    {
        Validate(session);
        AtomicFile.Write(Path.Combine(DirectoryFor(session.Id), "state.json"), JsonSerializer.SerializeToUtf8Bytes(session, Json));
    }
    public ProfilePreviewSession Load(Guid id)
    {
        var session = Read<ProfilePreviewSession>(Path.Combine(DirectoryFor(id), "state.json"));
        Validate(session);
        if (session.Id != id) throw new InvalidDataException("Preview identity does not match its journal.");
        return session;
    }
    public IReadOnlyList<ProfilePreviewSession> Incomplete()
    {
        if (!Directory.Exists(_root)) return [];
        return Directory.EnumerateDirectories(_root).Select(Path.GetFileName)
            .Where(name => Guid.TryParseExact(name, "N", out _))
            .Select(name => Guid.ParseExact(name!, "N"))
            .Where(id => File.Exists(Path.Combine(DirectoryFor(id), "state.json")))
            .Select(Load).Where(s => s.NeedsRecovery).ToArray();
    }
    public void Request(Guid id, string action)
    {
        if (action is not ("confirm" or "revert")) throw new ArgumentException("Unsupported preview action.");
        _ = Load(id);
        // Separate immutable requests: revert wins if both arrived before the worker's tick.
        string path = Path.Combine(DirectoryFor(id), action + ".request");
        if (!File.Exists(path))
        {
            try { AtomicFile.Write(path, [], overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
    }
    public bool Requested(Guid id, string action) => File.Exists(Path.Combine(DirectoryFor(id), action + ".request"));
    public static T Read<T>(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 256 * 1024) throw new InvalidDataException("Preview document is too large.");
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        try { return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Preview document is empty."); }
        catch (JsonException ex) { throw new InvalidDataException("Preview document is invalid.", ex); }
    }
    private static void Validate(ProfilePreviewSession s)
    {
        if (s.SchemaVersion != 1 || s.Id == Guid.Empty || s.Original is null
            || s.ExpiresAt.Year is < 2020 or > 2100) throw new InvalidDataException("Invalid preview journal.");
        if (s.Status is not ("prepared" or "applying" or "previewing" or "recovering" or "confirmed" or "reverted" or "superseded" or "recovery-pending"))
            throw new InvalidDataException("Invalid preview status.");
        IccProfileInspector.ValidateFileName(s.CandidateProfile);
        if (!ProfilePreviewPolicy.IsHash(s.CandidateSha256)
            || !ProfilePreviewPolicy.IsHash(s.Original.TopologyFingerprint)
            || s.Original.Profiles is null || s.Original.Profiles.Count > 256
            || !ProfilePreviewPolicy.Plan(s.Original, s.CandidateProfile).CanPreview
            || ProfilePreviewPolicy.ValidProfile(s.Original, s.CandidateProfile)?.Sha256 != s.CandidateSha256)
            throw new InvalidDataException("The journal does not contain a recoverable original association.");
    }
}
