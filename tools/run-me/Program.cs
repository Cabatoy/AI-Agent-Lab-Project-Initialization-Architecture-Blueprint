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

Console.WriteLine("AI Agent Lab | Depo inceleme ve devam rehberi");
Console.WriteLine("Bu araç komutları gösterir; dosya, Podman veya API işlemlerini kendisi çalıştırmaz.");
Console.WriteLine("Podman incelemesi için ayrı onay gerekir. Çıkmak için Ctrl+C veya quit kullanın.");

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

    if (command == "approve-podman")
    {
        if (state.CurrentStep != 2)
        {
            Console.WriteLine("Bu onay yalnızca Podman incelemesi adımında kullanılabilir.");
            continue;
        }

        state.PodmanInspectionApproved = true;
        state.CurrentStep++;
        SaveState(statePath, state);
        Console.WriteLine("Salt okunur Podman incelemesine izin verildi. Başlatma veya ayar değiştirme izni verilmedi.");
        continue;
    }

    if (command == "done")
    {
        if (state.CurrentStep == 2)
        {
            Console.WriteLine("Podman komutlarını göstermeden önce `approve-podman` yazıp salt okunur incelemeyi onaylayın.");
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
            Console.WriteLine("Rehber adımları ve Podman inceleme onayı sıfırlandı.");
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
        (state.CurrentStep >= 3 && !state.PodmanInspectionApproved))
    {
        throw new InvalidDataException($"İlerleme dosyası geçersiz: {statePath}. Dosyayı düzeltin veya silip yeni akış başlatın.");
    }

    return state;
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
            Console.WriteLine("@workspace Önce salt okunur depo denetimi yap. `.github/copilot-instructions.md`, `README.md`, `docs/continuation.md`, karar/açık-soru/yol-haritası belgeleri, `docker-compose.yml`, `scripts/` ve üç `.csproj` dosyasını incele; `git status --short` çalıştır. `bin/` ve `obj/` çıktıları hariç tut. Podman, build, ağ, dosya değişikliği veya kurulum komutu çalıştırma. Doğrulanmış durumu ve tutarsızlıkları özetle.");
            break;
        case 1:
            Console.WriteLine("@workspace `.github/copilot-instructions.md` ve `docs/continuation.md` dosyalarını oku. Handoff'taki durumu mevcut repo ile karşılaştır, önerilen devam noktasını doğrula ve sıradaki işi kısaca bildir. Henüz dosya değiştirme veya Podman komutu çalıştırma; kullanıcı yönlendirmesini bekle.");
            break;
        case 2:
            Console.WriteLine("Podman incelemesi henüz onaylanmadı.");
            Console.WriteLine("`approve-podman` yalnızca aşağıdaki salt okunur durum kontrollerini göstermek için izin verir.");
            Console.WriteLine("Bu onay machine başlatma/durdurma, ayar değiştirme, build, volume silme veya prune izni değildir.");
            break;
        case 3:
            Console.WriteLine("@workspace Yalnızca onaylanmış salt okunur Podman durum kontrolünü yap:");
            Console.WriteLine("  podman --version");
            Console.WriteLine("  podman machine list");
            Console.WriteLine("  podman compose version");
            Console.WriteLine("  podman compose ps");
            Console.WriteLine("Machine durmuşsa başlatma; durumu raporlayıp dur. `scripts/local.ps1 status` machine başlatabileceği için kullanma.");
            Console.WriteLine("Başlatma, ayar değişikliği, build, up/down, volume silme veya prune yapma.");
            Console.WriteLine("Sonuçları Copilot sohbetine gönderin; sonraki işlem için ayrıca konuşup onay verin.");
            break;
    }

    if (state.CurrentStep < 2)
    {
        Console.WriteLine("Adımı tamamladığınızı onaylayıp ilerlemek için `done` yazın.");
    }
    else if (state.CurrentStep == 2)
    {
        Console.WriteLine("Salt okunur Podman incelemesine izin vermek için `approve-podman` yazın; istemiyorsanız `quit` ile çıkın.");
    }
    else
    {
        Console.WriteLine("Kontrolleri tamamladıktan sonra `done` yazın. Bu rehber Podman'ı başlatmaz.");
    }
}

static void PrintHelp()
{
    Console.WriteLine("Komutlar:");
    Console.WriteLine("  help, ?          Tüm komutları göster");
    Console.WriteLine("  status            Adımlardaki ilerlemeyi göster");
    Console.WriteLine("  done              Mevcut depo/sohbet adımını tamamla");
    Console.WriteLine("  approve-podman    Salt okunur Podman durum kontrolünü onayla");
    Console.WriteLine("  reset             Rehber ilerlemesini sıfırla (onay istenir)");
    Console.WriteLine("  quit, exit        Kaydedilmiş ilerlemeyle çık");
    Console.WriteLine("  Ctrl+C            Güvenli biçimde çık; ilerleme korunur");
}

internal sealed class WorkflowState
{
    public int WorkflowVersion { get; set; }
    public int CurrentStep { get; set; }
    public bool PodmanInspectionApproved { get; set; }
}

internal static class Workflow
{
    public const int Version = 2;

    public static readonly string[] Steps =
    [
        "Dosyaları salt okunur kontrol et",
        "Copilot sohbetini handoff'tan sürdür",
        "Podman kontrolü için izin al",
        "Onaylı Podman durumunu kontrol et"
    ];
}