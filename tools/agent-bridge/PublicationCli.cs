namespace Hic.AgentBridge;

public static class PublicationCli
{
    public static int Run(string[] args)
    {
        var prepare = args[0] == "prepare-publication";
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        var allowedFields = new[] { "--repo", "--workspace", "--source-repository", "--destination",
            "--branch", "--project", "--approval-digest", "--offline-remote", "--credential-helper" };
        var allowedFlags = new[] { "--approve-github-read", "--approve-github-push", "--approve-publication" };
        for (var index = 1; index < args.Length; index++)
        {
            if (allowedFlags.Contains(args[index]))
            {
                if (!flags.Add(args[index]))
                    throw new BridgeException("DUPLICATE_PUBLICATION_FLAG");
                continue;
            }
            if (!allowedFields.Contains(args[index]) || index + 1 == args.Length
                || !fields.TryAdd(args[index], args[++index]))
                throw new BridgeException("INVALID_PUBLICATION_ARGUMENT");
        }
        string Required(string key) => fields.TryGetValue(key, out var value)
            ? value : throw new BridgeException("MISSING_PUBLICATION_ARGUMENT");
        if (prepare && (flags.Contains("--approve-github-push") || flags.Contains("--approve-publication")
            || fields.ContainsKey("--approval-digest"))
            || !prepare && (fields.ContainsKey("--project") || flags.Contains("--approve-github-read")))
            throw new BridgeException("PUBLICATION_STAGE_ARGUMENT_MISMATCH");
        var target = new PublicationTarget(Required("--destination"), Required("--branch"));
        var local = fields.TryGetValue("--offline-remote", out var localPath);
        PublicationTransport transport;
        if (local)
        {
            if (fields.ContainsKey("--credential-helper") || flags.Any(f => f.StartsWith("--approve-github-", StringComparison.Ordinal)))
                throw new BridgeException("OFFLINE_TRANSPORT_ARGUMENT_MISMATCH");
            transport = PublicationTransport.Local(target, localPath!);
        }
        else
        {
            var approvedNetwork = prepare ? flags.Contains("--approve-github-read") : flags.Contains("--approve-github-push");
            transport = PublicationTransport.GitHub(target, Required("--credential-helper"), approvedNetwork);
        }
        var publisher = new SnapshotPublisher(Required("--repo"), Required("--workspace"),
            Required("--source-repository"), target, transport, transport.CredentialHelper);
        if (prepare)
            Console.Write(publisher.Prepare(Required("--project"), DateTimeOffset.UtcNow).Text);
        else
        {
            var receipt = publisher.Publish(Required("--approval-digest"),
                flags.Contains("--approve-publication"), DateTimeOffset.UtcNow);
            Console.WriteLine(System.Text.Encoding.UTF8.GetString(CanonicalJson.Serialize(receipt)));
        }
        return 0;
    }
}
