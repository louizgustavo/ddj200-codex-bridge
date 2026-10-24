# Segurança e privacidade

## Modelo local

A ponte abre um servidor USB/IP exclusivamente em `127.0.0.1:3240`. Não há telemetria, conta própria, upload de logs nem API remota. A execução normal lê `config.toml` localmente para validar a estrutura necessária ao Micro. O assistente de primeira execução pode acrescentar campos Micro ausentes, com backup local ao lado do arquivo original; nunca publique esses backups. Preferências existentes não são substituídas.

## Driver USB/IP

USBip-win2 é um projeto independente e inclui componentes em modo kernel. A instalação, atualização ou remoção do driver pode afetar dispositivos USB. O instalador 1.1 incorpora o pacote oficial 0.9.8.0 x64, verificado por SHA-256 e assinatura no build. Na máquina de destino, confere novamente o hash antes de elevar somente o instalador oficial. A interface explica a interrupção temporária de USB e a necessidade de reiniciar. Uma dependência existente funcional é preservada; uma instalação quebrada não é substituída automaticamente.

Não desative antivírus, Secure Boot, integridade de memória nem exigência de assinatura para executar esta ponte. Não use builds de driver que exijam Test Signing em uma máquina de uso normal.

## Dados locais

Configuração e diagnósticos ficam em `%LOCALAPPDATA%\DDJ200CodexBridge`. Eles são preservados durante atualização/desinstalação para evitar perda inesperada e podem ser apagados manualmente. Antes de anexar logs a um issue, remova nomes de usuário, caminhos pessoais e qualquer conteúdo que identifique tarefas.

O endereço Bluetooth escolhido existe apenas na sessão atual e não é gravado. A descoberta não persiste inventário de dispositivos. O serviço e a característica BLE-MIDI são validados antes de qualquer entrada ou LED, e somente mensagens de LED documentadas com valor ligado/desligado são aceitas.

## Relato responsável

Evite publicar segredos ou dados pessoais em issues. Para uma vulnerabilidade, use o mecanismo privado de security advisory do GitHub quando ele estiver habilitado no repositório.
