# Segurança (Windows Credential Manager), Remotes (3 Modos), Wizard e Exportação Portátil

## Contexto

Para permitir que a interface funcione como ponte completa entre o usuário e o Rclone sem depender do terminal e sem expor credenciais ou chaves entre máquinas, foram implementados quatro pilares integrados:

1. **Cofre AES-256-GCM no Gerenciador de Credenciais do Windows** (`WindowsCredentialVault` + `SensitiveDataProtector`).
2. **Gerenciamento visual de Remotes (`rclone.conf`) com 3 modos de autenticação** (Link OAuth, Token JSON Manual e Chave/Credenciais).
3. **Exportação portátil sem credenciais nem chave mestra** (`ExportedConfigBundle`).
4. **Assistente Inicial (`FirstRunWizardWindow`) e Reset granular (`ResetConfigWindow`)**.

---

## Alterações Realizadas

### 1. Cofre AES-256-GCM (`src/RcloneManager.Core/Security/`)
- **`WindowsCredentialVault.cs`**:
  - Usa P/Invoke nativo em `advapi32.dll` (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`) com `CRED_TYPE_GENERIC` e `CRED_PERSIST_LOCAL_MACHINE`.
  - No startup, `GetOrCreateMasterKey` gera automaticamente uma chave de 32 bytes (`RandomNumberGenerator.GetBytes(32)`) em `RcloneManager:MasterKey` caso ainda não exista.
  - `RegenerateMasterKey` e `DeleteMasterKey` permitem rotacionar ou remover a chave.
- **`SensitiveDataProtector.cs`**:
  - Criptografia autenticada **AES-256-GCM** (`new AesGcm(key, 16)`) com nonce aleatório de 12 bytes e tag de 16 bytes, gravada com prefixo `enc:v1:`.
- **`AppConfigStore.cs`**:
  - Ao salvar `rclone_manager_config.json`, cifra em repouso `RcloneConfigPath`, `ProtectedSecrets` e `job.ExtraFlags`, descriptografando em memória ao carregar.

### 2. Gerenciamento Visual de Remotes (`rclone.conf`) e 3 Modos de Autenticação
- **Modelos (`Models/RcloneRemoteModels.cs`)**: `RemoteAuthMode` (`OAuthBrowserLink`, `ManualTokenJson`, `KeyOrCredentials`), `RcloneProviderInfo`, `RcloneRemoteDetail` (mascara segredos como `"••••••••"`).
- **Motor Híbrido (`RcloneHybridEngine.cs` + `RcloneRcClient.cs`)**:
  - Carrega dinamicamente os **71 provedores** via `/config/providers` (com fallback `rclone config providers`).
  - **Via Link (`OAuthBrowserLink`)**: abre o navegador automaticamente via `rclone config create` / `rclone config reconnect`; para Google Drive (`drive`), exibe orientação contextual para configurar `client_id` + `client_secret` próprios.
  - **Via Token JSON Manual (`ManualTokenJson`)**: valida a sintaxe JSON antes de salvar e inclui botão **Gerar Token via `rclone authorize`**.
  - **Via Chave / Credenciais (`KeyOrCredentials`)**: salva via RC `/config/create` ou `/config/update` com `--obscure` sem abrir navegador; opções de arquivo (`service_account_file`, `key_file`, certificados) ganham botão **Procurar...** automático na aba **Remotes (`rclone.conf`)**.

### 3. Exportação Portátil + Importação Guiada pelo Wizard
- **`AppConfigStore.ExportPortableConfig`**:
  - Exporta um `.json` (`Format = "RcloneManager.PortableConfig"`) contendo release travada, portas/opções, tarefas sanitizadas (`SanitizeExtraFlags` remove flags contendo `pass`, `secret`, `token`, `key`, `auth`, `header`) e `ExportedRemoteBlueprint` (apenas nome, tipo, modo de autenticação sugerido e parâmetros não sensíveis como `scope`, `host`, `port`, `endpoint`, `region`, `url`, `provider`).
  - **Nunca exporta** `RcloneManager:MasterKey`, `ProtectedSecrets`, `RcloneConfigPath`, tokens OAuth ou senhas.
- **`FirstRunWizardWindow.xaml` / `.cs`** (4 etapas):
  1. **Importação opcional + Nova Chave**: permite importar um `.json` exportado, gera/rotaciona uma nova chave AES-256 no Gerenciador de Credenciais do Windows e exibe o resumo das tarefas e serviços apontados.
  2. **Release Travada**: aplica e baixa/valida a versão travada do Rclone (SHA-256).
  3. **Autenticar Serviços Apontados**: lista os remotes do arquivo importado como `Pendente de autenticação`, pré-preenche nome/provedor/parâmetros públicos e conduz autenticação + teste um a um.
  4. **Revisão das Tarefas**: permite revisar nome, operação, origem, destino, intervalo e ativação de cada tarefa antes de salvar definitivamente e recarregar o serviço (`POST /api/service/reload`).
- **`ResetConfigWindow.xaml` / `.cs`** (`POST /api/service/reset`):
  - Reseta `rclone_manager_config.json` permitindo escolher se remove logs/runtime, a chave `RcloneManager:MasterKey` no Windows Credential Manager, e (com confirmação extra e backup `.bak`) o `rclone.conf`.

---

## Validacao

1. **Build completo `.NET 10` (`Release`)**:
   ```powershell
   dotnet build c:\rclone\RcloneManager.sln -c Release
   ```
   Resultado observado: `Compilação com êxito. 0 Aviso(s), 0 Erro(s)`.
2. **Testes automatizados de cofre e exportação portátil (`.NET 10`)**:
   - `WindowsCredentialVault` + `SensitiveDataProtector`: `KeyCreated=True`, `Exists=True`, `PrefixOk=True`, `TamperDetected=True`, `KeyRotated=True`, `AfterDeleteExists=False`.
   - `AppConfigStore.ExportPortableConfig` + `ReadPortableBundle`: `NoSecretsLeaked=True` (confirmado que tokens, `client_secret`, `ProtectedSecrets`, `--sftp-pass`, `enc:v1:`, `MasterKey` e `rcloneConfigPath` não constam no JSON exportado), `HasBlueprintAndSafeFlags=True` e `BundleLoadedOk=True`.
3. **Testes E2E no serviço (`RcloneManager.Service.exe`)**:
   - `GET /api/status`: `encryptionKeyActive: true`, `encryptionKeyTarget: "RcloneManager:MasterKey"`, `rcDaemonRunning: true`.
   - `GET /api/remotes/providers`: `71` provedores via `RC API (porta 5572)`.
   - `POST /api/remotes/save`: `InvalidManualTokenRejected=True` para JSON malformado no modo `ManualTokenJson`, e criação/teste/exclusão limpa nos modos `KeyOrCredentials` e RC API.
   - `POST /api/service/reload`: `Reloaded: True`.

---

## Observacoes / Pontos de atencao

- O `rclone.conf` continua sendo mantido pelo próprio `rclone.exe` (que obscurece senhas com `--obscure` e gerencia renovação de tokens OAuth). Na UI, segredos existentes retornados pelo `/config/dump` são mascarados como `"••••••••"` e não são sobrescritos se deixados em branco durante a edição.
- Ao importar uma configuração exportada em outra máquina, é esperado e obrigatório reautenticar os serviços listados na Etapa 3 do Wizard, pois nenhuma credencial sai da máquina de origem.
