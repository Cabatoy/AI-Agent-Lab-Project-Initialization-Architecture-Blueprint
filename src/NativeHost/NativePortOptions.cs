internal static class NativePortOptions
{
    internal const int DefaultPort = 19481;

    internal static int Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return DefaultPort;
        if (text.Length is < 4 or > 5 || text.Any(c => c is < '0' or > '9') ||
            !int.TryParse(text, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("HIC_NATIVE_PORT must be a decimal port in range 1024..65535.");
        return port;
    }
}
