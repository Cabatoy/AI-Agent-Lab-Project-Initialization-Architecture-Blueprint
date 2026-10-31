using System.Text.Json;

var repositoryRoot = FindRepositoryRoot();
var statePath = Path.Combine(repositoryRoot, ".run-me-state.json");
var state = LoadState(statePath);
var stopRequested = false;

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopRequested = true;
};

if (args.Length > 0)
{
    PrintHelp();
    return;
}

Console.WriteLine("AI Agent Lab | Copilot yerel akış rehberi");
Console.WriteLine("Bu araç komutları gösterir; Podman veya API çağrılarını kendisi çalıştırmaz.");
Console.WriteLine("İlerleme her değişiklikte kaydedilir. Çıkmak için Ctrl+C veya quit kullanın.");

while (!stopRequested)
{
    PrintProgress(state);
    PrintCurrentStep(state);
    Console.Write("run-me> ");

    var input = Console.ReadLine();
    if (input is null)
    {
        break;
    }

    var command = input.Trim();
    if (command.Length == 0)
    {
        continue;
    }

    if (command is "help" or "?" or "--help")
    {
        PrintHelp();
        continue;
    }

    if (command == "status")
    {
        PrintProgress(state);
        continue;
    }

    if (command is "quit" or "exit")
    {
        break;
    }

    if (command.StartsWith("url ", StringComparison.OrdinalIgnoreCase))
    {
        SetControlPlaneUrl(command[4..].Trim(), state, statePath);
        continue;
    }

    if (command.StartsWith("task ", StringComparison.OrdinalIgnoreCase))
    {
        SetTaskId(command[5..].Trim(), state, statePath);
        continue;
    }

    if (command == "done")
    {
        if (state.CurrentStep == 0 && state.ControlPlaneUrl is null)
        {
            Console.WriteLine("Önce scriptin yazdırdığı adresi `url <adres>` komutuyla kaydedin.");
            continue;
        }

        if (state.CurrentStep == 1 && state.TaskId is null)
        {
            Console.WriteLine("Önce Copilot yanıtındaki task ID'yi `task <id>` komutuyla kaydedin.");
            continue;
        }

        if (state.CurrentStep < Workflow.Steps.Length)
        {
            state.CurrentStep++;
            SaveState(statePath, state);
        }

        continue;
    }

    if (command == "reset")
    {
        Console.Write("Yalnızca rehber ilerlemesi sıfırlanacak; yerel task verisi etkilenmez. Emin misiniz? [y/N] ");
        var confirmation = Console.ReadLine();
        if (confirmation is "y" or "Y" or "yes" or "YES")
        {
            state = new WorkflowState();
            SaveState(statePath, state);
            Console.WriteLine("Rehber ilerlemesi sıfırlandı.");
        }

        continue;
    }

    Console.WriteLine("Komut tanınmadı. Kullanılabilir komutlar için `help` yazın.");
}

Console.WriteLine("Rehber kapatıldı. Kaydedilmiş ilerleme bir sonraki çalıştırmada devam eder.");

static string FindRepositoryRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
    {
        if (File.Exists(Path.Combine(directory.FullName, "docker-compose.yml")) &&
            Directory.Exists(Path.Combine(directory.FullName, "tools", "run-me")))
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException("Repo kökü bulunamadı. run.me dosyasını depo kökünden başlatın.");
}

static WorkflowState LoadState(string statePath)
{
    if (!File.Exists(statePath))
    {
        return new WorkflowState();
    }

    var state = JsonSerializer.Deserialize<WorkflowState>(File.ReadAllText(statePath));
    if (state is null || state.CurrentStep < 0 || state.CurrentStep > Workflow.Steps.Length)
    {
        throw new InvalidDataException($"İlerleme dosyası geçersiz: {statePath}. Dosyayı düzeltin veya silip yeni akış başlatın.");
    }

    return state;
}

static void SaveState(string statePath, WorkflowState state)
{
    var temporaryPath = statePath + ".tmp";
    File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(temporaryPath, statePath, true);
}

static void PrintProgress(WorkflowState state)
{
    Console.WriteLine();
    for (var index = 0; index < Workflow.Steps.Length; index++)
    {
        var marker = index < state.CurrentStep ? "x" : index == state.CurrentStep ? ">" : " ";
        Console.WriteLine($"[{marker}] {index + 1}/{Workflow.Steps.Length} {Workflow.Steps[index]}");
    }

    if (state.CurrentStep == Workflow.Steps.Length)
    {
        Console.WriteLine("Akış tamamlandı.");
    }
}

static void PrintCurrentStep(WorkflowState state)
{
    if (state.CurrentStep == Workflow.Steps.Length)
    {
        Console.WriteLine("Yeni bir tur için `reset`, komut listesi için `help` yazın.");
        return;
    }

    Console.WriteLine();
    Console.WriteLine($"Adım {state.CurrentStep + 1}: {Workflow.Steps[state.CurrentStep]}");
    Console.WriteLine("Copilot Chat'e gönderilecek içerik:");

    switch (state.CurrentStep)
    {
        case 0:
            Console.WriteLine("/new-build");
            Console.WriteLine("Scriptin yazdırdığı Control Plane adresini `url <adres>` ile kaydedin.");
            break;
        case 1:
            Console.WriteLine($"@workspace POST {state.ControlPlaneUrl}/tasks adresine {{ \"title\": \"Local workflow testi\", \"description\": \"Copilot akış kontrolü\" }} gövdesiyle task oluştur. Henüz onaylama; dönen task ID'yi bildir.");
            Console.WriteLine("Task ID geldikten sonra `task <id>` yazın, ardından `done` ile ilerleyin.");
            break;
        case 2:
            Console.WriteLine($"@workspace POST {state.ControlPlaneUrl}/tasks/{state.TaskId}/approve çağrısıyla bu task'i onayla ve sonucu bildir.");
            Console.WriteLine("Copilot onayı doğruladıktan sonra `done` yazın.");
            break;
        case 3:
            Console.WriteLine($"@workspace GET {state.ControlPlaneUrl}/tasks/{state.TaskId} çağrısıyla task durumunu kontrol et. `Completed` olana kadar tekrar kontrol et.");
            Console.WriteLine("Durum `Completed` olduğunda `done` yazın.");
            break;
    }

    Console.WriteLine("Adımı tamamladığınızı onaylayıp ilerlemek için `done` yazın.");
}

static void SetControlPlaneUrl(string value, WorkflowState state, string statePath)
{
    if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
        (address.Scheme != Uri.UriSchemeHttp && address.Scheme != Uri.UriSchemeHttps) ||
        address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.Query) || !string.IsNullOrEmpty(address.Fragment))
    {
        Console.WriteLine("Geçerli bir kök adres girin. Örnek: url http://localhost:8080");
        return;
    }

    state.ControlPlaneUrl = address.ToString().TrimEnd('/');
    SaveState(statePath, state);
    Console.WriteLine($"Control Plane adresi kaydedildi: {state.ControlPlaneUrl}");
}

static void SetTaskId(string value, WorkflowState state, string statePath)
{
    if (!Guid.TryParse(value, out var taskId))
    {
        Console.WriteLine("Task ID geçerli bir GUID olmalı.");
        return;
    }

    state.TaskId = taskId.ToString();
    SaveState(statePath, state);
    Console.WriteLine($"Task ID kaydedildi: {state.TaskId}");
}

static void PrintHelp()
{
    Console.WriteLine("Komutlar:");
    Console.WriteLine("  help, ?          Tüm komutları göster");
    Console.WriteLine("  status            Adımlardaki ilerlemeyi göster");
    Console.WriteLine("  url <adres>       Control Plane kök adresini kaydet");
    Console.WriteLine("  task <guid>       Oluşturulan task ID'sini kaydet");
    Console.WriteLine("  done              Mevcut adımı onayla ve ilerle");
    Console.WriteLine("  reset             Rehber ilerlemesini sıfırla (onay istenir)");
    Console.WriteLine("  quit, exit        Kaydedilmiş ilerlemeyle çık");
    Console.WriteLine("  Ctrl+C            Güvenli biçimde çık; ilerleme korunur");
}

internal sealed class WorkflowState
{
    public int CurrentStep { get; set; }
    public string? ControlPlaneUrl { get; set; }
    public string? TaskId { get; set; }
}

internal static class Workflow
{
    public static readonly string[] Steps =
    [
        "Stack'i başlat",
        "Task oluştur",
        "Task'i onayla",
        "Task sonucunu doğrula"
    ];
}