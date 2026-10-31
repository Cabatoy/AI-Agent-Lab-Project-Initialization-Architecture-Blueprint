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

    switch (state.CurrentStep)
    {
        case 0:
            Console.WriteLine("Önce Copilot'un projeyi tanımasını sağlayacağız. Henüz hiçbir servis başlatılmayacak.");
            PrintChatMessage("Bu projeyi yeni açtım. Önce .github/copilot-instructions.md dosyasını, README.md dosyasını ve proje dosyalarını incele. Proje ne yapıyor, eksik veya çelişkili dosya var mı, kısa ve anlaşılır anlat. Sadece dosyaları oku; hiçbir şeyi değiştirme, kurma veya başlatma. Podman, build veya ağ isteği çalıştırma.");
            break;
        case 1:
            Console.WriteLine("Şimdi Copilot'a projede en son nerede kaldığımızı okutacağız.");
            PrintChatMessage("docs/continuation.md dosyasını oku ve yazılanları mevcut proje dosyalarıyla karşılaştır. Neler tamamlanmış, neler bekliyor ve sırada ne öneriliyor? Kısaca anlat. Henüz değişiklik yapma veya komut çalıştırma; benim kararımı bekle.");
            break;
        case 2:
            Console.WriteLine("İsterseniz şimdi Podman'ın kurulu olup olmadığını ve servislerin durumunu kontrol ettirebiliriz.");
            Console.WriteLine("Bu kontrol hiçbir şeyi başlatmaz, durdurmaz, değiştirmez veya silmez.");
            Console.WriteLine("Bu adımda Chat'e mesaj göndermeniz gerekmiyor; kararınızı bu konsola yazın.");
            break;
        case 3:
            Console.WriteLine("Durum kontrolüne izin verdiniz. Copilot sonucu anlatacak; sonraki işlemi birlikte kararlaştıracağız.");
            PrintChatMessage("Podman durumunu kontrol etmene izin veriyorum. Yalnızca podman --version, podman machine list, podman compose version ve podman compose ps komutlarını kullan. Podman kurulu değilse veya makine durmuşsa durumu anlatıp dur. Hiçbir şeyi başlatma, durdurma, değiştirme veya silme. scripts/local.ps1 status ve scripts/local.sh status kullanma; bunlar değişiklik yapabilir. Sonucu anlaşılır biçimde özetle ve sonraki işlem için kararımı bekle.");
            break;
    }

    if (state.CurrentStep < 2)
    {
        Console.WriteLine("Copilot'un yanıtını bekleyin ve okuyun.");
        Console.WriteLine("Sonra BU KONSOLA dönüp done yazın ve Enter'a basın. done komutunu Chat'e göndermeyin.");
    }
    else if (state.CurrentStep == 2)
    {
        Console.WriteLine("İzin vermek için BU KONSOLA approve-podman yazıp Enter'a basın.");
        Console.WriteLine("İstemiyorsanız quit yazın. Bu onay yalnızca durum kontrolü içindir.");
    }
    else
    {
        Console.WriteLine("Copilot'un yanıtını okuyunca BU KONSOLA done yazıp Enter'a basın.");
    }
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
        "Copilot'a projeyi tanıt",
        "Nerede kaldığımızı öğren",
        "Podman durum kontrolüne izin ver",
        "Podman durumunu Copilot'a kontrol ettir"
    ];
}