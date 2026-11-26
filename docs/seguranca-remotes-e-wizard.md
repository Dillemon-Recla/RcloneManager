# Segurança com Chave Descartável, Arquitetura SOLID, Remotes (3 Modos) e Wizard

## Contexto

Originalmente, a Etapa 1 de `FirstRunWizardWindow` expunha um card técnico da chave AES-256 (`Target: RcloneManager:MasterKey`) com o botão *"Gerar Nova Chave"*, e `ResetConfigWindow` exibia um checkbox manual para excluir a chave no Cofre do Windows. Além disso, `AppConfigStore` concentrava múltiplas responsabilidades (persistência JSON, criptografia de campos, fábrica de configuração padrão, exportação/importação portátil e reset destrutivo), enquanto `FirstRunWizardWindow.xaml.cs` e `MainWindow.xaml.cs` acoplavam diretamente primitivas estáticas de criptografia e motor híbrido.

Além do ruído de UX, rotacionar a chave na abertura do Wizard (`Loaded`) trazia risco de invalidar campos `enc:v1:...` já gravados em disco caso o usuário fechasse o assistente sem salvar.

---

## Alteracoes realizadas

### 1. Módulo de Segurança Desacoplado e Chave Descartável (`src/RcloneManager.Core/Security/`)
- **`IMasterKeyStore.cs` e `WindowsCredentialVault.cs` (`WindowsCredentialMasterKeyStore`)**:
  - Abstração de armazenamento de chave de 32 bytes com implementação Win32 via `advapi32.dll` (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`) e implementação em memória (`InMemoryMasterKeyStore`) para testes isolados sem acoplamento ao SO.
- **`ISensitiveDataProtector.cs` e `SensitiveDataProtector.cs` (`AesGcmDataProtector`)**:
  - Criptografia autenticada **AES-256-GCM** (`enc:v1:`) injetável via `ISensitiveDataProtector`, mantendo fachadas estáticas compatíveis.
- **`Models/ConfigCloningExtensions.cs`**:
  - Centraliza `.Clone()` para `JobSchedule`, `SyncJob` e `AppManagerConfig`, eliminando duplicação entre agendador, persistência e exportador.

### 2. Decomposição SOLID de Armazenamento (`src/RcloneManager.Core/Storage/`)
- **`IAppConfigServices.cs`**: define contratos segregados (`IConfigCryptoTransformer`, `IDefaultConfigFactory`, `IPortableConfigService`, `IConfigResetService` e `IAppConfigRepository`).
- **`ConfigCryptoTransformer.cs`**:
  - Antes: `AppConfigStore` inicializava a chave indiscriminadamente e misturava clonagem/cifragem.
  - Agora: detecta presença de dados sensíveis (`HasSensitiveData`), cria a chave silenciosamente sob demanda e suporta **rotação atômica da chave descartável** (`ProtectForStorage(config, rotateDisposableKey: true)`) exatamente no momento do `Save()`, impedindo ciphertext órfão se o usuário cancelar janelas intermediárias.
- **`DefaultConfigFactory.cs`**: isola criação da configuração padrão, preenchimento de caminhos e detecção de `rclone.conf`.
- **`PortableConfigService.cs`**: isola exportação/leitura de `ExportedConfigBundle`, sanitização de flags sensíveis (`pass`, `secret`, `token`, `key`, `auth`, `header`) e inferência de `ExportedRemoteBlueprint`, garantindo que nenhum segredo, token ou chave saia da máquina.
- **`ConfigResetService.cs`**: isola o reset de workspace e descarta/recria automaticamente a chave descartável por padrão (`ResetConfigRequest.DeleteCredentialKey = true`).
- **`AppConfigStore.cs`**: reduzido a repositório focado em leitura/escrita atômica (`rclone_manager_config.json`), compondo os serviços acima.

### 3. Desacoplamento do Wizard, Remotes e Injeção de Dependência (`Onboarding`, `Service` e `UI`)
- **`Onboarding/RemoteAuthRequestBuilder.cs`**: construtor puro de domínio que valida e mapeia `WizardRemoteAuthInput` + `ExportedRemoteBlueprint` para `CreateOrUpdateRemoteRequest` (incluindo mapeamento por provedor como `s3`, `drive` e `webdav`).
- **`UI/Services/WizardOnboardingService.cs` (`IWizardOnboardingService`)**:
  - Orquestra estado do onboarding, importação em memória, verificação de release, listagem/autenticação de remotes (priorizando `ServiceApiClient` com fallback local para `RcloneHybridEngine`) e conclusão com `Save(CurrentConfig, rotateDisposableKey: true)`.
- **`FirstRunWizardWindow.xaml` / `.cs` e `ResetConfigWindow.xaml` / `.cs`**:
  - Removidos o card e o botão manual *"Gerar Nova Chave"* da Etapa 1 do Wizard (agora **"1. Início & Importação"**) e o checkbox técnico da `MasterKey` na janela de Reset.
  - Removido todo acoplamento direto da UI (`MainWindow` e `FirstRunWizardWindow`) com `RcloneManager.Core.Security`.
- **`Service/Program.cs` e `Scheduling/JobScheduler.cs`**:
  - Registro explícito das interfaces no contêiner de DI do ASP.NET Core (`IMasterKeyStore`, `ISensitiveDataProtector`, `IConfigCryptoTransformer`, `IDefaultConfigFactory`, `IPortableConfigService`, `IAppConfigRepository`, `IConfigResetService`).

---

## Validacao

1. **Compilação completa `.NET 10` (`Release`)**:
   ```powershell
   dotnet build c:\rclone\RcloneManager.sln -c Release
   ```
   Resultado observado: `Compilação com êxito. 0 Aviso(s), 0 Erro(s)`.
2. **Suíte automatizada de módulos desacoplados (`InMemoryMasterKeyStore` + `AesGcmDataProtector` + `AppConfigStore` + `WizardOnboardingService` + `RemoteAuthRequestBuilder`)**:
   - Criação automática da chave ao persistir dados sensíveis e cifragem `enc:v1:` em disco.
   - Segurança ao cancelar o Wizard: abrir `WizardOnboardingService` e importar bundle em memória **não** altera a chave nem invalida o `rclone_manager_config.json` existente antes de `CompleteAsync`.
   - Rotação atômica ao concluir o Wizard (`CompleteAsync`) e descarte/recriação automática no `ResetConfig()`, preservando leitura íntegra dos dados salvos com a nova chave.
   - Exportação portátil (`PortableConfigService.ExportPortableBundle`) confirmada sem vazamento de `ProtectedSecrets`, senhas em flags CLI ou blocos `enc:v1:`.
   - Mapeamento `RemoteAuthRequestBuilder.Build` validado para `s3` (`endpoint`, `access_key_id`, `secret_access_key` + parâmetros públicos do blueprint).
   - Resultado observado: `ALL_SOLID_TESTS_PASSED=True`.

---

## Observacoes / Pontos de atencao

- O `rclone.conf` permanece gerenciado nativamente pelo `rclone.exe` (`--obscure` e renovação de tokens OAuth), enquanto `rclone_manager_config.json` protege `RcloneConfigPath`, `ProtectedSecrets` e `ExtraFlags` com a chave descartável local.
- Caso a credencial `RcloneManager:MasterKey` seja apagada externamente no Windows, `ConfigCryptoTransformer.UnprotectInPlace` limpa apenas os campos `enc:v1:` ilegíveis sem impedir a inicialização do serviço ou da UI, gerando nova chave automaticamente no próximo salvamento.
