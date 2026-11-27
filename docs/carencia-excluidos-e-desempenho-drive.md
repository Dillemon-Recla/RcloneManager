# Carência de 30 Dias para Excluídos, Expurgo Imediato e Desempenho no Google Drive

## Contexto e Causa Raiz

Em execuções reais de backup da pasta do perfil do usuário (`C:\Users\<usuario> -> gdrive:BackupPC/`), foram observados três comportamentos críticos:

1. **Queda severa de taxa de transferência (`~100–823 B/s`) e `HTTP 403 RATE_LIMIT_EXCEEDED`**:
   - Remotes do Google Drive autenticados sem `client_id` próprio utilizam o projeto OAuth global compartilhado do Rclone (`project_number:202264815644`). Rajadas simultâneas sem controle de TPS disparam `403 Quota exceeded` e *exponential backoff*, agravado pela criação sequencial de milhares de micro-arquivos (~500 bytes) de pastas de jogos/caches.
2. **Pegadinha de pastas ocultas na raiz (`- **/.*/**`)**:
   - Na sintaxe de filtros do Rclone, `- **/.*/**` exige uma barra antes do ponto e não bloqueia pastas ocultas diretamente na raiz da origem (ex.: `.ravion/`, `.gemini/`, `.claude/`), exigindo `- .*/**` e `- .*`.
3. **Arquivos antigos órfãos após novas regras de exclusão**:
   - Sem `--delete-excluded`, pastas que já haviam subido antes de entrarem na lista de exclusão permaneciam indefinidamente na nuvem; com `--delete-excluded` direto no `sync`, qualquer exclusão acidental apagaria dados da nuvem na hora, sem margem de recuperação.

---

## Alteracoes realizadas

### 1. Rastreamento de Carência de 30 Dias e Expurgo sob Demanda (`RcloneManager.Core` e `RcloneManager.Service`)
- **`Engine/FilterFileHelper.cs`**:
  - **`BuildOptimizedContent(...)`**: expande automaticamente `- **/.*/**` e `- **/.*` para incluir também `- .*/**` e `- .*`, blindando pastas e arquivos ocultos na raiz.
  - **`BuildInvertedExcludedOnlyFilterContent(string filterFilePath, FilterRuleMode mode)`**: gera o complemento booleano exato (*First-Match-Wins*) de qualquer arquivo `--filter-from` ou `--exclude-from`, selecionando no destino exclusivamente itens hoje excluídos e protegendo (`- ...`) todos os itens incluídos no backup.
- **`Engine/ExcludedRetentionManager.cs`**:
  - Rastreia localmente em `.rclone-manager-runtime\excluded_retention.json` a data `FirstExcludedUtc` de cada arquivo já presente no destino que passou a bater nas regras de exclusão (`rclone lsf --files-only -R --fast-list --filter-from <invertido>`), **sem mover nem duplicar objetos na nuvem** (zero consumo extra de armazenamento ou cópias).
  - Ao vencer o prazo configurável (`SyncJob.ExcludedRetentionDays`, padrão `30` dias com `EnableExcludedGraceRetention = true`), remove apenas os caminhos vencidos via `rclone delete --files-from-raw` + limpeza *best-effort* de diretórios vazios (`rclone rmdirs --leave-root`).
  - **`PurgeAllExcludedNowAsync(...)`**: executa o expurgo imediato de todos os itens excluídos presentes no destino e limpa o registro de carência da tarefa.
- **`Scheduling/JobScheduler.cs` e `Service/Program.cs`**:
  - Após sincronizações concluídas com sucesso (`Sync` / `Copy`), agenda avaliação automática de carência a cada janela mínima de 12 horas por tarefa.
  - Novos endpoints locais: `GET /api/jobs/{id}/excluded-retention?scan=false|true` e `POST /api/jobs/{id}/purge-excluded`.

### 2. Flags Padrão Anti-Throttling para Google Drive (`DefaultConfigFactory` e `FirstRunWizardWindow`)
- Tarefas padrão e tarefas criadas pelo Wizard apontando para Google Drive agora incluem por padrão:
  `--tpslimit 6 --tpslimit-burst 6 --drive-chunk-size 64M --retries 3 --low-level-retries 5`, estabilizando a cadência de requisições HTTP (evitando `403 RATE_LIMIT_EXCEEDED` mesmo sem `client_id` próprio) e reduzindo round-trips em arquivos médios/grandes.

### 3. Seletor Claro do Serviço no Wizard e Suporte Completo a `WinService` (`UI`, `Service` e `Security`)
- **`FirstRunWizardWindow.xaml` / `.cs` e `WizardOnboardingService.cs`**:
  - Etapa 4 passa a exibir um seletor explícito com descrição dinâmica clara entre:
    1. `UserStartup` (*Iniciar com meu usuário do Windows — Recomendado, sem pedir Admin*);
    2. `WindowsService` (*Instalar Serviço Nativo do Windows — Servidor / antes do login, pede UAC Admin 1 vez*);
    3. `ManualOnly` (*Somente manual — funciona apenas ao abrir o aplicativo*).
- **`ServiceControlHelper.cs` e `ServiceLauncher.cs`**:
  - `InstallAndStartWindowsService(...)` para instâncias prévias, executa `sc config` (se já instalado) ou `sc create`, configura política de auto-recuperação (`sc failure`) e inicia imediatamente via `sc start RcloneManagerService` após liberar a porta `5580` da sessão local, com fallback seguro caso o UAC seja recusado.
- **`Security/WindowsCredentialVault.cs`**:
  - Mantém espelho local protegido via DPAPI de máquina (`CryptProtectData` com `CRYPTPROTECT_LOCAL_MACHINE` em `%ProgramData%\RcloneManager\vault_*.dpapi`) para que o serviço rodando sob conta `LocalSystem` consiga decifrar `rclone_manager_config.json` sem perder `RcloneConfigPath` ou `ExtraFlags`.

---

## Validacao

Compilação completa da solução e teste automatizado de integração em `.NET 10` com `rclone.exe` (`v1.76.0`):

```powershell
dotnet build C:\rclone\RcloneManager.sln -c Debug --nologo
```

Cenários verificados diretamente contra `rclone.exe`:
- **Scan inicial de carência**: detectou exatamente os arquivos excluídos no destino (`.ravion/sprite1.png`, `Documents/Ravion/Ravion-Client/assets/mob1.png` e executável bloqueado por `- *`), preservando `DaysRemaining = 30` e `Purged = 0`.
- **Expiração seletiva (> 30 dias)**: ao retroceder apenas `.ravion/sprite1.png` para 31 dias, `ScanAndEvaluateAsync` removeu automaticamente somente o item vencido e manteve os demais excluídos em carência.
- **Expurgo imediato (`PurgeAllExcludedNowAsync`)**: removeu os itens excluídos restantes e preservou intactos 100% dos arquivos válidos (`contrato.pdf` e `foto.png`).

---

## Observacoes / Pontos de atencao

- **Cota de criação da API do Google Drive**: o Google Drive impõe limite estrutural de ~2–3 novos arquivos criados por segundo. Pastas com dezenas de milhares de micro-arquivos (como sprites soltos de jogos, `node_modules`, `bin`, `obj` e `packages`) devem permanecer nas regras de exclusão ou ser compactadas em arquivo único antes do envio.
- **Permissão UAC no modo `WinService`**: a opção `WindowsService` no Wizard solicita elevação administrativa uma única vez; se cancelada pelo usuário, o onboarding reverte automaticamente para `UserStartup` (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) sem interromper o funcionamento.
