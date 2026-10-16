# CS2 Bot Improver & Skin Simulator

Mod completo e aprimorado para **Counter-Strike 2** focado em melhorar drasticamente a inteligência artificial, movimentação, táticas e reações dos bots em partidas offline/locais, além de integração com simulador de inventário e skins.

Atualizado e compatível com a versão mais recente do Counter-Strike 2 (**v1.41.9.1+ / Outubro de 2026**).

---

## 🚀 Principais Funcionalidades

### 🧠 Bots Inteligentes & Táticos
* **Perfis Personalizados de Bots (`botprofile.vpk` / `botprofile.db`):** Reações, agressividade, mira e movimentação ajustadas para simular jogadores reais competitivos em todos os níveis.
* **Comportamentos Aprimorados (`BotAI` & `BotState`):** Tomada de decisão, posicionamento no mapa, cobertura e rush customizados.
* **Sistema de Mira Dinâmica (`BotAimImprover`):** Mira e spray progressivos configuráveis via `bot_aim` (ex: `bot_aim mixed`, `bot_aim head`).
* **Utilitários & Granadas Realistas (`NadeSystem`):** Arremesso inteligente de smookes, flashes e molotovs com táticas predefinidas.
* **Economia & Compras Aprimoradas (`BotBuy` & `bot_buy.cfg`):** Bots compram utilitários e armas de acordo com a economia da partida.
* **Recapitulativo de Dano (`RoundDamageRecap`):** Feedback detalhado de dano causado/recebido ao término de cada round.

### 🎨 Simulador de Skins & Inventário
* **InventorySimulator (v3.7.0):** Integração para carregar skins personalizadas, facas, luvas, adesivos e agentes em tempo real durante suas partidas locais (compatível com `inventory.cstrike.app` / comando `!ws`).

---

## 📂 Estrutura do Mod

```
CS2BotImprover/
├── addons/
│   ├── metamod/              # Metamod:Source (build git1481 compatível com CS2 1.41.9.1)
│   ├── counterstrikesharp/   # CounterStrikeSharp (v1.0.376) + Plugins (.NET 8)
│   │   ├── plugins/          # BotAI, BotState, BotAimImprover, NadeSystem, InventorySimulator, etc.
│   │   ├── configs/          # Configurações de administração e plugins
│   │   └── gamedata/         # Assinaturas e offsets atualizados
│   ├── BotController/        # Módulo nativo de controle dos bots
│   ├── BotHider/             # Módulo de controle de visibilidade
│   └── RayTrace/             # Suporte a traçado de raios / linha de visão dos bots
├── cfg/
│   ├── my_bot_normal_config.cfg  # Configuração customizada competitiva dos bots
│   ├── my_bot_ffa_config.cfg     # Configuração customizada para Deathmatch / FFA
│   ├── bot_buy.cfg               # Lista de compras prioritárias
│   └── gamemode_*.cfg            # Modos de jogo com hooks automáticos de carregamento
├── overrides/
│   ├── botprofile.vpk        # VPK montado com o banco de dados refinado dos bots
│   └── botprofile.db         # Definições de cada personalidade e atributos de bots
├── backup/
│   ├── WithBots/gameinfo.gi  # gameinfo.gi com montagem dos addons e overrides
│   └── Online/gameinfo.gi    # gameinfo.gi padrão limpo do CS2 para jogar online com VAC
├── gameinfo.gi               # gameinfo.gi pré-configurado
├── Panel v1.4.3.exe          # Utilitário gráfico para alternar entre modo Bots e modo Online
└── README.md
```

---

## 🛠️ Instalação

1. Localize a pasta de instalação do seu Counter-Strike 2:
   ```text
   <Sua_SteamLibrary>/steamapps/common/Counter-Strike Global Offensive/game/csgo/
   ```
2. Copie o conteúdo deste repositório (`addons`, `cfg`, `overrides`, `gameinfo.gi`) diretamente para a pasta `game/csgo/`, substituindo os arquivos solicitados.
3. Inicie o CS2 normalmente ou use o executável `Panel v1.4.3.exe` para alternar entre o modo com Bots e o modo Online (sem mods).

---

## ⌨️ Comandos Úteis no Console

* `exec my_bot_normal_config.cfg` — Força o carregamento manual dos parâmetros competitivos dos bots.
* `bot_aim mixed` / `bot_aim head` — Alterna a precisão de mira dos bots.
* `bot_nades normal` — Habilita uso tático de granadas.
* `!ws` (no chat) — Abre o menu/atualiza suas skins do simulador de inventário.
