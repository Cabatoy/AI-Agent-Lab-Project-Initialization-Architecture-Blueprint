# Temiz Başlangıç (Windows PowerShell)

Yeni önerilen tek komut akışı:

```powershell
# Depo kökünde çalıştırın.
./scripts/local.ps1 bootstrap-dev -Reset
```

Bu komut local stack'i sıfırlar, yeniden kurar, AI Lab bulunursa `ai-lab` alias'ını bağlar, `.env` dosyasını yazar ve smoke test çalıştırır.

Manuel adım adım akış gerekiyorsa aşağıdaki komut setini kullanın:

```powershell
# Depo kökünde çalıştırın.

# 1) Tam temizle (veri silinir)
./scripts/local.ps1 delete-all -ConfirmDataRemoval

# 2) .env yoksa örnekten oluştur
if (!(Test-Path .\.env)) { Copy-Item .\.env.example .\.env }

# 3) Stack'i sıfırdan kur/başlat
./scripts/local.ps1 new-build

# 4) Durum kontrolü
./scripts/local.ps1 status

# 5) Smoke test (ID otomatik)
$base = "http://localhost:8080" # status çıktısına göre gerekirse güncelleyin
Set-Content -Path .\task.json -Encoding utf8 -Value '{"title":"LLM smoke clean start","description":"kisa bir uygulama plani uret"}'
$task = curl.exe -s -X POST "$base/tasks" -H "Content-Type: application/json" --data-binary "@.\task.json" | ConvertFrom-Json
$taskId = $task.id
curl.exe -s -X POST "$base/tasks/$taskId/approve" | Out-Host
1..10 | % {
  Start-Sleep -Seconds 3
  $s = curl.exe -s "$base/tasks/$taskId" | ConvertFrom-Json
  "$( $_ ): $($s.status)"
  if ($s.status -in @("Completed","Failed")) { $s | ConvertTo-Json -Depth 6; break }
}

# 6) (opsiyonel) worker log
podman logs --tail 200 agentlab-worker
```

Beklenen final sonuç: `status=Completed` ve `result` dolu.
