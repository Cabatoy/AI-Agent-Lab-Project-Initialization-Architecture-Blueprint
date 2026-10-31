# Local Copilot Akış Rehberi

`run.me`, Agent Lab local task smoke test'ini Copilot Chat ile adım adım yürütmek için komut önerileri gösterir. Podman veya Control Plane API çağrılarını kendisi çalıştırmaz; her adımı kullanıcı Copilot Chat'te uygular ve `done` yazarak onaylar.

## Önkoşullar

- .NET 8 SDK
- Podman Desktop ve `podman compose` tarafından kullanılabilen bir Compose provider
- VS Code Copilot Chat

Podman kurulumu ve Windows/WSL ağ onayı için ana [README](../README.md#local-podman-kurulumu) adımlarını izleyin.

## Başlatma

Depo kökünden:

Windows PowerShell:

```powershell
./run.me.ps1
```

macOS veya Linux:

```bash
bash ./run.me
```

## Akış

1. Rehber `/new-build` promptunu gösterir. Promptu Copilot Chat'te çalıştırın. Windows/WSL user-mode networking onayı gerekirse promptun ayrı onay adımını izleyin.
2. Scriptin yazdırdığı Control Plane adresini `url <adres>` komutuyla kaydedin. Windows'ta bu adres `localhost` yerine Podman machine IP'si olabilir.
3. Rehberin gösterdiği task oluşturma talimatını Copilot Chat'e gönderin. Dönen GUID'yi `task <guid>` komutuyla kaydedin.
4. Sonraki adımlarda task'i onaylatın ve `GET /tasks/{id}` ile `Completed` durumunu doğrulatın. Her adımdan sonra `done` yazın; sonraki talimat ancak bu onaydan sonra gösterilir.

Bu prototipte Worker task içeriğini çalıştırmaz; Redis kuyruk ve durum geçişi smoke test'i yapıp task'i tamamlandı olarak işaretler.

## Rehber Komutları

| Komut | İşlev |
|---|---|
| `help` veya `?` | Tüm komutları gösterir |
| `status` | Adım ilerlemesini gösterir |
| `url <adres>` | Control Plane kök adresini kaydeder |
| `task <guid>` | Task ID'sini kaydeder |
| `done` | Mevcut adımı onaylar ve ilerler |
| `reset` | Onay alarak yalnızca rehber ilerlemesini sıfırlar |
| `quit` veya `exit` | Kaydedilmiş ilerlemeyle çıkar |
| `Ctrl+C` | Güvenli biçimde çıkar; ilerleme korunur |

İlerleme depo kökündeki `.run-me-state.json` dosyasına kaydedilir ve Git tarafından yok sayılır. `reset`, Podman container'larına, volume'lere veya task kayıtlarına dokunmaz. `rebuild-all` ve `delete-all` bu rehber akışının parçası değildir; veri silme için README ve ilgili Chat promptlarındaki açık onay kapılarını izleyin.