# Temiz Ba?lang?ç (Windows PowerShell)

Yeni önerilen tek komut ak???:

```powershell
cd C:\Enterprise
./scripts/local.ps1 bootstrap-dev -Reset
```

Bu komut local stack'i s?f?rlar, yeniden kurar, AI Lab bulunursa `ai-lab` alias'?n? ba?lar, `.env` dosyas?n? yazar ve smoke test çal??t?r?r.

Manuel ad?m ad?m ak?? gerekiyorsa a?a??daki komut setini kullan?n:

```powershell
# 0) Repo köküne geç
cd C:\Enterprise

# 1) Tam temizle (veri silinir)
./scripts/local.ps1 delete-all -ConfirmDataRemoval

# 2) .env yoksa örnekten olu?tur
if (!(Test-Path .\.env)) { Copy-Item .\.env.example .\.env }

# 3) Stack'i s?f?rdan kur/ba?lat
./scripts/local.ps1 new-build

# 4) Durum kontrolü
./scripts/local.ps1 status

# 5) Smoke test (ID otomatik)
$base = "http://172.21.95.108:8080"
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
