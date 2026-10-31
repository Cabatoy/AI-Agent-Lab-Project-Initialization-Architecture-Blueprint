using System.Diagnostics;
using System.Text.Json;

var repositoryRoot = FindRepositoryRoot();
var statePath = Path.Combine(repositoryRoot, ".run-me-state.json");
var promptsPath = Path.Combine(repositoryRoot, "tools", "run-me", "prompts.tr.json");
var prompts = LoadPrompts(promptsPath);
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

Console.WriteLine("AI Agent Lab | Depo inceleme ve devam rehberi");
Console.WriteLine("Dosya/handoff adımları yalnızca Chat mesajı gösterir; onaylandıktan sonra Podman durum kontrolü ve gerekirse setup/onarım bu araç tarafından çalıştırılır.");
Console.WriteLine("Chat mesajları `tools/run-me/prompts.tr.json` dosyasından okunur.");
Console.WriteLine("Podman incelemesi ve setup/onarım için ayrı açık onay gerekir. Çıkmak için Ctrl+C veya quit kullanın.");

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

    if (TryGetStepNumberCommand(command, out var stepNumber))
    {
        PrintStepShortcut(stepNumber, prompts);
        continue;
    }

    if (TryGetPromptCommand(command, out var promptKey))
    {
        PrintPromptMessage(promptKey, prompts);
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

    if (command is "approve-podman" or "approve podman")
    {
        if (state.CurrentStep != 2)
        {
            Console.WriteLine("Bu onay yalnızca Podman incelemesi adımında kullanılabilir.");
            continue;
        }

        state.PodmanInspectionApproved = true;
        state.CurrentStep++;
        SaveState(statePath, state);
        Console.WriteLine("Salt okunur Podman incelemesine izin verildi. Şimdi durum kontrolü çalıştırılıyor.");
        var inspectionHealthy = RunPodmanInspection();
        if (state.CurrentStep < Workflow.Steps.Length)
        {
            state.CurrentStep++;
            if (inspectionHealthy)
            {
                Console.WriteLine("Podman kurulumu sağlıklı; setup/onarım adımı gerekmiyor.");
                state.CurrentStep = Workflow.Steps.Length;
            }
            SaveState(statePath, state);
        }
        continue;
    }

    if (command is "approve-setup" or "approve setup")
    {
        if (state.CurrentStep != 4)
        {
            Console.WriteLine("Bu onay yalnızca Podman setup/onarım adımında kullanılabilir.");
            continue;
        }

        state.PodmanSetupApproved = true;
        SaveState(statePath, state);
        Console.WriteLine("Podman setup/onarım komutunu çalıştırma izni verildi. Komut başlatılıyor.");
        RunPodmanSetupRepair(repositoryRoot);
        state.CurrentStep = Workflow.Steps.Length;
        SaveState(statePath, state);
        continue;
    }

    if (command == "done")
    {
        if (state.CurrentStep == 2)
        {
            Console.WriteLine("Podman komutlarını göstermeden önce `approve-podman` yazıp salt okunur incelemeyi onaylayın.");
            continue;
        }

        if (state.CurrentStep == 4 && !state.PodmanSetupApproved)
        {
            state.CurrentStep = Workflow.Steps.Length;
            SaveState(statePath, state);
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
            state = NewState();
            SaveState(statePath, state);
            Console.WriteLine("Rehber adımları ve onaylar sıfırlandı.");
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
        return NewState();
    }

    var state = JsonSerializer.Deserialize<WorkflowState>(File.ReadAllText(statePath));
    if (state is null || state.WorkflowVersion != Workflow.Version)
    {
        Console.WriteLine("Rehber akışı güncellendi; eski ilerleme yeni adımlarla uyumlu olmadığından baştan başlatılıyor.");
        var newState = NewState();
        SaveState(statePath, newState);
        return newState;
    }

    if (state.CurrentStep < 0 || state.CurrentStep > Workflow.Steps.Length ||
        (state.CurrentStep >= 3 && !state.PodmanInspectionApproved) ||
        (state.CurrentStep == 5 && !state.PodmanSetupApproved))
    {
        throw new InvalidDataException($"İlerleme dosyası geçersiz: {statePath}. Dosyayı düzeltin veya silip yeni akış başlatın.");
    }

    return state;
}

static Dictionary<string, string> LoadPrompts(string promptsPath)
{
    if (!File.Exists(promptsPath))
    {
        throw new FileNotFoundException($"Prompt dosyası bulunamadı: {promptsPath}");
    }

    var prompts = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(promptsPath));
    if (prompts is null || prompts.Count == 0)
    {
        throw new InvalidDataException($"Prompt dosyası boş veya geçersiz: {promptsPath}");
    }

    return prompts;
}

static WorkflowState NewState() => new() { WorkflowVersion = Workflow.Version };

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

static bool TryGetStepNumberCommand(string command, out int stepNumber)
{
    stepNumber = 0;
    var normalized = NormalizeCommand(command);

    if (!normalized.StartsWith("step "))
    {
        return false;
    }

    return int.TryParse(normalized[5..], out stepNumber);
}

static bool TryGetPromptCommand(string command, out string promptKey)
{
    promptKey = string.Empty;
    var normalized = NormalizeCommand(command);

    return normalized switch
    {
        "repo review" => SetPromptKey("repo-review", out promptKey),
        "handoff check" => SetPromptKey("handoff-check", out promptKey),
        "podman check" => SetPromptKey("podman-check", out promptKey),
        "setup repair" => SetPromptKey("setup-repair", out promptKey),
        _ => false
    };
}

static bool SetPromptKey(string key, out string promptKey)
{
    promptKey = key;
    return true;
}

static string NormalizeCommand(string command)
{
    var compact = string.Join(' ', command.Trim().ToLowerInvariant().Replace('ı', 'i').Split(' ', StringSplitOptions.RemoveEmptyEntries));
    return compact;
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

    switch (state.CurrentStep)
    {
        case 0:
            Console.WriteLine("Chat'e göndermek için `repo review` yazın.");
            break;
        case 1:
            Console.WriteLine("Chat'e göndermek için `handoff check` yazın.");
            break;
        case 2:
            Console.WriteLine("Bu adım onay adımıdır. Devam için `approve-podman` yazın.");
            break;
        case 3:
            Console.WriteLine("Bu adım `approve-podman` onayı sırasında otomatik çalıştırılır.");
            break;
        case 4:
            Console.WriteLine("Podman kontrolünde sorun bulundu: devam için `approve-setup` yazın, atlamak için `done` yazın.");
            break;
        case 5:
            Console.WriteLine("Bu adım `approve-setup` onayı sırasında otomatik çalıştırılır.");
            break;
    }

    if (state.CurrentStep < 2)
    {
        Console.WriteLine("Copilot'un yanıtını bekleyin ve okuyun. Sonra BU KONSOLA `done` yazın.");
    }
    else if (state.CurrentStep == 2)
    {
        Console.WriteLine("Bu onay yalnızca salt okunur durum kontrolü içindir.");
    }
    else if (state.CurrentStep == 4)
    {
        Console.WriteLine("Setup/onarım izni ayrı onay gerektirir.");
    }
    else
    {
        Console.WriteLine("Copilot'un yanıtını okuyunca BU KONSOLA `done` yazın.");
    }
}

static void PrintStepShortcut(int stepNumber, Dictionary<string, string> prompts)
{
    switch (stepNumber)
    {
        case 1:
            PrintPromptMessage("repo-review", prompts);
            return;
        case 2:
            PrintPromptMessage("handoff-check", prompts);
            return;
        case 3:
            Console.WriteLine("Adım 3 onay adımıdır. `approve-podman` yazın.");
            return;
        case 4:
            PrintPromptMessage("podman-check", prompts);
            return;
        case 5:
            Console.WriteLine("Adım 5 karar/onay adımıdır. Sorun varsa `approve-setup`, yoksa `done` yazın.");
            return;
        case 6:
            PrintPromptMessage("setup-repair", prompts);
            return;
        default:
            Console.WriteLine("Geçersiz adım numarası. Kullanım: `step <1-6>`.");
            return;
    }
}

static void PrintPromptMessage(string promptKey, Dictionary<string, string> prompts)
{
    if (!prompts.TryGetValue(promptKey, out var message) || string.IsNullOrWhiteSpace(message))
    {
        Console.WriteLine($"Prompt tanımı bulunamadı: `{promptKey}`. `tools/run-me/prompts.tr.json` dosyasını kontrol edin.");
        return;
    }

    PrintChatMessage(message);
}

static void PrintChatMessage(string message)
{
    Console.WriteLine();
    Console.WriteLine("Visual Studio veya VS Code'da Copilot Chat penceresini açın.");
    Console.WriteLine("Aşağıdaki iki çizgi arasındaki metni kopyalayıp Chat'e gönderin:");
    Console.WriteLine("---------------- CHAT MESAJI ----------------");
    Console.WriteLine(message);
    Console.WriteLine("---------------------------------------------");
    Console.WriteLine();
}

static bool RunPodmanInspection()
{
    Console.WriteLine();
    Console.WriteLine("Salt okunur Podman durum kontrolü çalıştırılıyor...");

    var versionOk = RunCommand("podman", "--version");
    var machineOk = RunCommand("podman", "machine list");
    var composeOk = RunCommand("podman", "compose version");
    var psOk = RunCommand("podman", "compose ps");

    var healthy = versionOk && machineOk && composeOk && psOk;
    if (!healthy)
    {
        Console.WriteLine();
        Console.WriteLine("Podman kurulumunda veya makine durumunda eksik/hata tespit edildi.");
    }

    return healthy;
}

static void RunPodmanSetupRepair(string repositoryRoot)
{
    Console.WriteLine();
    Console.WriteLine("Repository lifecycle scripti çalıştırılıyor (new-build)...");

    if (OperatingSystem.IsWindows())
    {
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "local.ps1");
        RunCommand("pwsh", $"-File \"{scriptPath}\" new-build", repositoryRoot);
    }
    else
    {
        var scriptPath = Path.Combine(repositoryRoot, "scripts", "local.sh");
        RunCommand("bash", $"\"{scriptPath}\" new-build", repositoryRoot);
    }

    Console.WriteLine();
    Console.WriteLine("Windows/WSL user-mode networking değişikliği gerekiyorsa script durup bildirir;");
    Console.WriteLine("bu durumda etkisini onayladıktan sonra `-AllowWslNetworkingChange` ile scripti elle yeniden çalıştırın.");
}

static bool RunCommand(string fileName, string arguments, string? workingDirectory = null)
{
    Console.WriteLine();
    Console.WriteLine($"> {fileName} {arguments}");

    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
        WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory
    };

    try
    {
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.WriteLine($"Komut başlatılamadı: {fileName}");
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Komut çalıştırılamadı: {ex.Message}");
        return false;
    }
}

static void PrintHelp()
{
    Console.WriteLine("Komutlar:");
    Console.WriteLine("  help, ?            Tüm komutları göster");
    Console.WriteLine("  status             Adımlardaki ilerlemeyi göster");
    Console.WriteLine("  step <n>           İstenen adımı doğrudan çağırır (1-6)");
    Console.WriteLine("  repo review        Proje dosya inceleme promptu");
    Console.WriteLine("  handoff check      continuation karşılaştırma promptu");
    Console.WriteLine("  podman check       Salt okunur Podman kontrol promptu (referans; approve-podman otomatik çalıştırır)");
    Console.WriteLine("  setup repair       Setup/onarım promptu (referans; approve-setup otomatik çalıştırır)");
    Console.WriteLine("  done               Mevcut depo/sohbet adımını tamamla");
    Console.WriteLine("  approve-podman     Salt okunur Podman durum kontrolünü onayla ve hemen çalıştır");
    Console.WriteLine("  approve-setup      Setup/onarım komutunu onayla ve hemen çalıştır (scripts/local.*  new-build)");
    Console.WriteLine("  reset              Rehber ilerlemesini sıfırla (onay istenir)");
    Console.WriteLine("  quit, exit         Kaydedilmiş ilerlemeyle çık");
    Console.WriteLine("  Ctrl+C             Güvenli biçimde çık; ilerleme korunur");
}

internal sealed class WorkflowState
{
    public int WorkflowVersion { get; set; }
    public int CurrentStep { get; set; }
    public bool PodmanInspectionApproved { get; set; }
    public bool PodmanSetupApproved { get; set; }
}

internal static class Workflow
{
    public const int Version = 5;

    public static readonly string[] Steps =
    [
        "Copilot'a projeyi tanıt (repo review)",
        "Nerede kaldığımızı öğren (handoff check)",
        "Podman durum kontrolüne izin ver (approve-podman)",
        "Podman durumu otomatik kontrol edildi (podman check)",
        "Sorun varsa setup/onarım izni ver (approve-setup)",
        "Setup/onarım otomatik uygulandı (setup repair)"
    ];
}