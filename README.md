# Prelude Validator

Código do instalador Windows da versão 1.2.0.0. O arquivo distribuído como `Prelude Validator.exe` tem SHA-256 `fbbfe247418465800c2b25d0573cb030b74c82a8f5a7714f08c348c502bafe76`.

O instalador localiza o The Isle Legacy, confere a versão do executável, guarda uma cópia do original e permite instalar ou restaurar o cliente Prelude.

## Compilar

Use o PowerShell no Windows com o compilador do .NET Framework 4.x. O script espera o cliente Prelude em `Support\TheIsleClient-Win64-Shipping.exe` e verifica seu SHA-256 antes de incorporá-lo ao instalador:

```powershell
.\build_native.ps1
```

O executável usado no build está em `Support`. O código-fonte dele não está neste projeto; os arquivos `.cs` são do Validator.

Para executar os testes locais:

```powershell
.\run_native_tests.ps1
```

Os resultados de compilação ficam em `dist` e `test-build`; essas pastas também ficam fora do Git.
