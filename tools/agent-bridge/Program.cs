using System.Security;
using System.Text;
using Hic.AgentBridge;

try
{
    if (args.Length > 0 && args[0] is "prepare-publication" or "publish-publication")
        return PublicationCli.Run(args);
    if (args.Length < 1 || args[0] is not ("generate" or "read"))
        throw new BridgeException("USAGE: generate --repo PATH --input RELATIVE_PATH --approval-sha256 SHA [--write-local] | read --repo PATH --project ID --expected-source SHA [--max-age-minutes N]");
    var command = args[0];
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    var write = false;
    for (var index = 1; index < args.Length; index++)
    {
        if (args[index] == "--write-local" && command == "generate" && !write)
        {
            write = true;
            continue;
        }
        var allowed = command == "generate"
            ? new[] { "--repo", "--input", "--approval-sha256", "--source-repository" }
            : new[] { "--repo", "--project", "--expected-source", "--max-age-minutes", "--source-repository" };
        if (!allowed.Contains(args[index]) || index + 1 == args.Length
            || !values.TryAdd(args[index], args[++index]))
            throw new BridgeException("INVALID_ARGUMENTS");
    }
    string Required(string key) => values.TryGetValue(key, out var value)
        ? value : throw new BridgeException("MISSING_ARGUMENT");
    var repo = Path.GetFullPath(Required("--repo"));
    var git = new LocalGit(repo, values.GetValueOrDefault("--source-repository", SnapshotProducer.Repository));
    var store = new ArtifactStore(repo);
    var now = DateTimeOffset.UtcNow;
    if (command == "generate")
    {
        var inputPath = Required("--input");
        Safety.Reference(inputPath);
        if (Path.GetExtension(inputPath) != ".json")
            throw new BridgeException("INPUT_JSON_REQUIRED");
        var input = store.ReadBounded(Path.Combine(repo, inputPath.Replace('/', Path.DirectorySeparatorChar)));
        var snapshot = new SnapshotProducer().Generate(input, Required("--approval-sha256"), git, now);
        if (write)
        {
            Console.WriteLine(store.Write(snapshot, now));
        }
        else
        {
            Console.WriteLine($"DRY_RUN_VALID snapshotId={snapshot.SnapshotId} bytes={CanonicalJson.Serialize(snapshot).Length}; no files written; not published.");
        }
    }
    else
    {
        var minutes = 1440;
        if (values.TryGetValue("--max-age-minutes", out var age)
            && (!int.TryParse(age, out minutes) || minutes is < 1 or > 10080))
            throw new BridgeException("INVALID_FRESHNESS_LIMIT");
        var snapshot = store.ReadLatest(Required("--project"), Required("--expected-source"), now,
            TimeSpan.FromMinutes(minutes), git);
        Console.WriteLine(Encoding.UTF8.GetString(CanonicalJson.Serialize(snapshot)));
    }
    return 0;
}
catch (BridgeException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (IOException)
{
    Console.Error.WriteLine("LOCAL_IO_FAILED: outcome may be partial; inspect local artifacts before retry; no automatic recovery.");
    return 1;
}
catch (UnauthorizedAccessException)
{
    Console.Error.WriteLine("LOCAL_ACCESS_DENIED");
    return 1;
}
catch (SecurityException)
{
    Console.Error.WriteLine("LOCAL_ACCESS_DENIED");
    return 1;
}
catch (System.ComponentModel.Win32Exception)
{
    Console.Error.WriteLine("GIT_START_FAILED");
    return 1;
}
catch (ArgumentException)
{
    Console.Error.WriteLine("INVALID_PATH_OR_ARGUMENT");
    return 1;
}
