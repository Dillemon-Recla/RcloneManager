# RcloneManager

> 🇧🇷 **Português** | 🇺🇸 [English](README.en.md)

Interface Desktop (**WPF**) e Serviço em segundo plano (**ASP.NET Core + Windows Service**) em **.NET 10** para controlar o [Rclone](https://rclone.org/) no Windows com motor híbrido (**API HTTP RC + CLI**), agendador automático de tarefas, telemetria de transferências ao vivo, otimizador de filtros e provisionamento automático com trava de release (**SHA-256**).

🌐 **Página do Projeto (GitHub Pages):** [dillemon-recla.github.io/RcloneManager](https://dillemon-recla.github.io/RcloneManager/)

---

## Principais Recursos

- **Motor Híbrido (RC API + CLI)** (`RcloneHybridEngine`): avalia cada tarefa caso a caso:
  - **CLI + Monitoramento RC ao vivo (`CliWithRcMonitoring`)**: executa `rclone.exe` com todas as flags nativas (`--filter-from`, `--max-size`, `--skip-links`, `--fast-list`, `--log-file`) e ativa `--rc --rc-no-auth` em porta dinâmica para ler progresso e ajustar banda ao vivo.
  - **Daemon RC persistente (`RcDaemonApi`)**: mantém `rclone rcd` na porta `5572` para consultas rápidas (`/config/listremotes`, `/core/version`, `/operations/about`) e operações diretas sem flags exclusivas de CLI.
  - **CLI puro (`PureCli`)**: fallback automático caso a porta RC não esteja disponível.
- **Interface Desktop + Serviço Desacoplado**:
  - Ao abrir a UI (`RcloneManager.UI`), ela sobe automaticamente o `RcloneManager.Service` em segundo plano (`--background`) caso ainda não esteja rodando.
  - Suporta minimizar para a **Bandeja do Sistema** (`TrayIconHelper`) ou fechar a interface mantendo o serviço e o agendador ativos.
- **Agendador Automático (`JobScheduler`)**:
  - Modos `IntervalMinutes`, `DailyAtTime`, `WeeklyDays` e `OnServiceStart`.
  - Fila sequencial com retentativas configuráveis em caso de falhas transitórias de rede/DNS (`RetryOnFailure`, `MaxRetries`, `RetryDelayMinutes`).
- **Assistente Inicial (First-Run Wizard) & Reset Simplificado**:
  - Na primeira execução (ou ao importar uma configuração), o Wizard (`FirstRunWizardWindow` + `WizardOnboardingService`) conduz: início limpo ou importação opcional `.json`, verificação/download da release travada, autenticação dos serviços apontados e revisão das tarefas — sem expor detalhes técnicos de criptografia ao usuário.
  - Opção dedicada (`ResetConfigWindow` + `ConfigResetService`) para resetar configurações, limpar logs/runtime e opcionalmente apagar `rclone.conf` (com backup `.bak`), descartando e recriando automaticamente a chave local descartável.
- **Gerenciamento Visual de Remotes (`rclone.conf`) com 3 Modos**:
  - Ponte direta para os **71+ provedores** do Rclone (`/config/providers` e `/config/dump`), com mascaramento de tokens, teste de conexão (`rclone lsd` + `/operations/about`) e terminal opcional `rclone config`.
  - Escolha explícita de autenticação: **Via Link (Navegador / OAuth)** (com orientação de `client_id` próprio no Google Drive), **Via Token JSON Manual** (com validação JSON e botão `rclone authorize`) ou **Via Chave / Credenciais** (S3, SFTP, senha ou `service_account_file` com seletor de arquivo).
- **Proteção Transparente com Chave Descartável & Exportação Portátil**:
  - Gerencia silenciosamente uma chave descartável **AES-256** no **Gerenciador de Credenciais do Windows** (`IMasterKeyStore` / `AesGcmDataProtector`) para cifrar dados sensíveis locais em repouso (`AES-256-GCM`, `enc:v1:...`), rotacionando-a atomicamente ao concluir o Wizard ou resetar.
  - A exportação (`PortableConfigService`) gera um arquivo portátil **sem credenciais, tokens ou chaves** — contendo apenas tarefas sanitizadas e o blueprint dos serviços utilizados para reautenticação guiada pelo Wizard em outra máquina.
- **Auto-Download e Trava de Release (`RcloneReleaseManager`)**:
  - Se `rclone.exe` não existir ou divergir da versão travada (padrão `v1.76.0`), a UI baixa o `.zip` oficial (`downloads.rclone.org` com fallback para GitHub Releases), valida o hash **SHA-256**, extrai e confirma a versão executando `rclone.exe version`.
- **Auto-Update em 2º Plano via GitHub Releases (`AppAutoUpdater`)**:
  - Consulta automaticamente novas releases estáveis no GitHub, baixa o pacote `.zip` portátil em segundo plano validando o hash **SHA-256** e aplica a atualização ao fechar ou reabrir o aplicativo, preservando todas as configurações e logs locais.
- **Analisador e Otimizador de Filtros (`FilterFileHelper`)**:
  - Detecta mistura de `--exclude-from` com regras `+ ` (`--filter-from`), sintaxe inline inválida (`- * [-- max-size ...]`) e problemas de precedência (*first-match-wins*), sanitizando automaticamente em tempo de execução e oferecendo correção definitiva com backup `.bak`.
- **Menus Clássicos & Janela Sobre**:
  - Barra de menus **Arquivo** e **Ajuda** (atalhos `Ctrl+N` / `Ctrl+S`, exportação/importação, diagnóstico e janela **Sobre** com tecnologias e licença MIT).
- **Zero dependências NuGet externas**: construído apenas com os frameworks nativos do SDK **.NET 10** (`net10.0` / `net10.0-windows`).

---

## Arquitetura

```mermaid
flowchart LR
    UI["RcloneManager.UI (WPF + System Tray)"] <-->|"HTTP JSON 127.0.0.1:5580"| SVC["RcloneManager.Service (ASP.NET Core + JobScheduler)"]
    UI -.->|"Verifica/Baixa (SHA-256)"| REL["RcloneReleaseManager"]
    SVC --> ENG["RcloneHybridEngine"]
    ENG -->|"CLI + --rc porta dinamica"| CLI["rclone.exe (sync / copy / move / check)"]
    ENG <-->|"HTTP RC 127.0.0.1:5572"| RCD["rclone rcd (Daemon Persistente)"]
```

---

## Estrutura do Repositório

```text
RcloneManager.sln
├── Iniciar-RcloneManager.bat               # Lançador rápido (compila em Release se necessário e abre a UI)
├── LICENSE                                 # Licença MIT
├── docs/                                   # Documentação técnica detalhada
└── src/
    ├── RcloneManager.Core/                 # Modelos, Motor Híbrido, Agendador, Cofre AES-256, Filtros e Release Manager
    ├── RcloneManager.Service/              # Serviço em 2º plano, API HTTP (:5580) e dispatcher Windows Service
    └── RcloneManager.UI/                   # Interface WPF (Dark UI), Wizard inicial, Remotes, Sobre e System Tray
```

---

## Pré-requisitos e Início Rápido

### Pré-requisitos
- **Windows 10 / 11** (`x64`, `arm64` ou `x86`)
- **.NET 10 SDK** (para compilar) ou **.NET 10 Desktop + ASP.NET Core Runtime** (para executar os binários compilados)

### Compilar e Executar

```powershell
git clone https://github.com/Dillemon-Recla/RcloneManager.git
cd RcloneManager

# Compilar toda a solução em modo Release
dotnet build RcloneManager.sln -c Release
```

Em seguida, execute **[Iniciar-RcloneManager.bat](Iniciar-RcloneManager.bat)** (ou abra diretamente `src\RcloneManager.UI\bin\Release\net10.0-windows\RcloneManager.UI.exe`):

1. Na primeira execução, o **Assistente Inicial (Wizard)** permite começar do zero ou importar uma configuração portátil (`.json`), verifica/baixa o `rclone.exe` travado (`v1.76.0`, SHA-256), conduz a autenticação dos remotes (via Link OAuth, Token JSON ou Chave/Credenciais) e exibe a revisão das tarefas, gerenciando a criptografia local automaticamente em segundo plano.
2. A UI inicia automaticamente o `RcloneManager.Service` em segundo plano na porta `127.0.0.1:5580` e mantém o agendador ativo mesmo ao minimizar ou fechar a janela.

---

## Documentação Técnica (`docs/`)

- [Arquitetura e Motor Híbrido (RC + CLI)](docs/arquitetura-e-motor-hibrido.md)
- [API HTTP Local e Modos do Serviço](docs/api-local-servico.md)
- [Segurança, Remotes (3 Modos), Wizard e Exportação Portátil](docs/seguranca-remotes-e-wizard.md)
- [Provisionamento Automático e Trava de Release](docs/provisionamento-e-trava-release.md)
- [Regras de Filtro e Otimizador](docs/filtros-e-otimizador.md)
- [Operação, Serviço Windows e Solução de Problemas](docs/operacao-e-servico-windows.md)

---

## Contribuições

Contribuições são muito bem-vindas! Você pode ajudar abrindo **Issues** (bugs, sugestões de melhoria ou dúvidas) ou enviando **Pull Requests** na branch `dev`.

---

## Apoie o Projeto

Se o **RcloneManager** facilita sua rotina de backups e sincronizações, você pode apoiar o desenvolvimento:

- **GitHub Sponsors (Internacional / Devs):** [github.com/sponsors/Dillemon-Recla](https://github.com/sponsors/Dillemon-Recla)
- **Ko-fi (Apoio avulso / Internacional):** [ko-fi.com/dillemonrecla](https://ko-fi.com/dillemonrecla)
- **Pix (Brasil — Chave Aleatória):** `95b9aa86-3c99-421d-9c86-6a47a3b09d7b`

<p>
  <img src="docs/assets/pix-qrcode.png" alt="QR Code Pix para apoiar o RcloneManager" width="180" />
</p>

---

## Licença

Distribuído sob a licença **MIT**. Consulte [LICENSE](LICENSE) para mais detalhes.
