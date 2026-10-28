# CS2 Bot Improver & Skin Simulator — Edição SS+ Ultra (v3.0.0)

Mod competitivo para **Counter-Strike 2** projetado para elevar a inteligência artificial, tomada de decisão por **Utility AI + HFSM**, balística 3D de utilitários via `RayTrace`, movimentação subtick sem colisão em cantos e disciplina de tiro ao nível de jogadores profissionais Tier 1, mantendo um desafio justo e humano.

Compatível com **Counter-Strike 2 (v1.41.9.1+ / Outubro de 2026)**.

---

## 🏆 O Que Há de Novo na Revolução v3.0.0 (SS+ Ultra)

### 1. `BotProTactics` v3.0.0 — Cérebro Híbrido Utility AI + HFSM & RayTrace Subtick
* **Arquitetura Utility AI + Máquina de Estados Hierárquica (7 Estados):** Avalia em tempo real (com escalonamento de ticks zero-GC para não impactar o FPS do servidor) funções de utilidade contínuas ($U \in [0, 1]$) entre `CombatDuel`, `TradeFrag`, `TacticalRetreat`, `ObjectiveControl`, `AngleSlicePeek`, `TacticalHold` e `PatrolAndPreAim`.
* **Counter-Strafing & Peeks Anti-Parede (`RayTrace` Lateral Clearance):** Antes de injetar `IN_MOVELEFT`, `IN_MOVERIGHT` ou `IN_BACK`, o bot sonda `58u` lateralmente e na retaguarda via `CRayTraceInterface` (`MASK_PLAYER_MOVE`), impedindo que os bots strafem contra paredes, fiquem presos em quinas ou recuem contra obstáculos.
* **Slicing-the-Pie 3D & Crosshair Placement em Desníveis:** Substituído o antigo travamento `LookPitch = 0` por cálculo dinâmico de inclinação vertical (`atan2(vel.Z, speed2D)`) para escadas/rampas e sonda tripla em leque (`-28°`, `0°`, `+28°`) para pré-mirar na abertura de corredores antes de dobrar esquinas.
* **Recuo Tático Inteligente (Troca para Pistola, Nunca Faca):** Quando o rifle zera o pente (`clip1 == 0`) ou o HP cai abaixo de `28`, o bot recua pela rota desobstruída e saca automaticamente a pistola secundária (corrigindo o bug anterior que puxava a faca `9001` no meio do tiroteio).
* **Controle de Spray Real em Todas as Distâncias (`< 850u`) e Curva de Mira Humana:** O contador `SprayTicks` agora é atualizado em cada disparo real (`OnWeaponFire`) em qualquer distância, aplicando compensação progressiva de recuo e curva sigmoidal de pulso limitada a velocidades humanas justas (`390°/s`).

### 2. `NadeSystem` v2.0.0 — Solucionador Balístico 3D em Tempo Real + Lineups Táticos
* **Solucionador Balístico Parabólico 3D (`TrySolveBallisticArc`) com `RayTrace` (`MASK_GRENADE`):** Calcula em tempo real o vetor de velocidade inicial $\mathbf{v}_0(T)$ sob a gravidade efetiva de granadas do Source 2 ($g_{\text{eff}} = 360\text{ u/s}^2$), validando 7 segmentos da parábola contra colisões de teto/parede e suportando **tabelas de 1 ricochete (1-Bounce Wall Bank)** usando a normal da superfície ($\hat{\mathbf{n}}$).
* **Smokes Dinâmicas Anti-Sniper & Molotov/HE de Flush:** Quando fora de zonas estáticas de JSON, os bots usam o solucionador balístico para cortar a linha de visão de AWPs inimigas com smokes ou desalojar inimigos escondidos atrás de cobertura com Molotov/HE.
* **Fim do Bloqueio de Smokes após 21s & Correção do Bug de Flash (`BlindUntilTime = 85f`):** Liberado o uso de utilitários de execução de meio/fim de round e retake, além de proibir arremessos suicidas enquanto o bot está exposto na linha de visão inimiga.

### 3. `BotState` v2.0.0 — Visão Justa na Smoke, Unstuck 8-Direções & Anti-Flash Humano
* **Remoção Total do Wallhack de Smoke (`SetSmokeLength(500f)`):** Removido o gatilho oculto que expandia `bot_max_visible_smoke_length` para `500` sempre que um bot sofria dano ou tocava na C4. Agora as smokes bloqueiam a visão de forma 100% justa (`0f`).
* **Recuperação de Cantos (Unstuck) Guiada por `RayTrace` em 8 Direções:** Quando um bot trava em uma quina ou porta, o `BotState` sonda 8 direções radiais ($0^\circ \dots 315^\circ$) até `145u` via `RayTrace` para encontrar o corredor livre mais amplo e redirecionar o vetor de velocidade.
* **Desvio de Flashbang com Tempo de Reação Humano (`<= 260ms`):** Os bots não viram mais de costas no instante em que a flash sai da mão do inimigo (`1.5s` antes de estourar); eles reagem na janela humana real (`120–260ms` antes da detonação) e nunca viram de costas se já estiverem trocando tiro com um inimigo visível.

### 4. `botprofile.db` & Presets VPK (`High`, `Medium`, `Low`) Calibrados
* **Física de Mira Criticamente Amortecida ($c \approx 2\sqrt{k}$):** Removidos todos os 22 perfis corrompidos com `LookAngleMaxAccelNormal = 999999...9.9.9.9` e `LookAngleStiffnessNormal = 3000.0` (que causavam *spinbot* e oscilação).
* **Tempos de Reação Reais & Fim do Gate `< 50 Skill`:** Reações ajustadas de `700ms` (`0.50s + 0.20s`) para `165–205ms` no preset **High (SS+)**, `210–260ms` no **Medium** e `270–330ms` no **Low**, além de elevar os templates `Rank*` de `Skill = 49` para `88–92` (evitando a árvore de decisão de baixa inteligência da engine).
* **Prioridade Estrita de Rifles Tier 1:** Templates `Duelist`, `Fastshot`, `Camper` e `Rusher` corrigidos para nunca largarem AK-47 ou M4A1-S no chão em troca de escopetas ou PP-Bizon.

---

## 📂 Estrutura do Mod

```text
CS2BotImprover/
├── addons/
│   ├── metamod/              # Metamod:Source compatível com CS2 1.41.9.1+
│   ├── counterstrikesharp/   # CounterStrikeSharp + Plugins (.NET 8)
│   │   ├── plugins/          # BotProTactics (v2.0.0 SS+), BotAI, BotState, BotAimImprover, BotBuy, NadeSystem
│   │   ├── configs/          # Configurações de plugins e administração
│   │   └── gamedata/         # Assinaturas e offsets binários
│   ├── BotController/        # Módulo nativo C++ de injeção de Usercmds e controle de bots
│   ├── BotHider/             # Módulo nativo de gerenciamento de visibilidade
│   └── RayTrace/             # Módulo nativo de RayTracing (linha de visão precisa)
├── cfg/
│   ├── my_bot_normal_config.cfg  # Configuração Competitiva SS+ (C4 40s, A* otimizado)
│   ├── my_bot_ffa_config.cfg     # Configuração Deathmatch / FFA SS+
│   ├── bot_buy.cfg               # Regras de decisão econômica e compra de armamento
│   └── gamemode_*.cfg            # Hooks automáticos de modo de jogo
├── overrides/
│   ├── botprofile.vpk        # VPK padrão (Preset SS+ High)
│   ├── botprofile.db         # Banco de dados fonte calibrado (SS+ High)
│   ├── High/botprofile.vpk   # Preset SS+ Pro Tier 1 (Reação 165-205ms, Skill 88-100)
│   ├── Medium/botprofile.vpk # Preset Competitivo / FACEIT Lvl 7-9 (Reação 210-260ms)
│   └── Low/botprofile.vpk    # Preset Casual / Aquecimento (Reação 270-330ms)
├── backup/
│   ├── WithBots/gameinfo.gi  # gameinfo.gi configurado para carregar addons e VPK overrides
│   └── Online/gameinfo.gi    # gameinfo.gi original limpo para partidas online (VAC)
├── gameinfo.gi               # gameinfo.gi pronto para uso com o mod
├── Panel v1.4.3.exe          # Painel gráfico para alternar entre modo Bots (High/Medium/Low) e Online
└── README.md
```

---

## 🛠️ Instalação

1. Localize a pasta `game/csgo/` dentro do diretório do seu Counter-Strike 2:
   ```text
   <Sua_SteamLibrary>/steamapps/common/Counter-Strike Global Offensive/game/csgo/
   ```
2. Extraia o conteúdo do pacote (`addons`, `cfg`, `overrides`, `backup`, `gameinfo.gi`, `Panel v1.4.3.exe`) diretamente em `game/csgo/`, substituindo os arquivos existentes.
3. Use o `Panel v1.4.3.exe` para escolher a dificuldade desejada (`High` para **SS+ Pro**, `Medium` para **Competitivo**, `Low` para **Casual**) ou alternar para o modo **Online** antes de jogar em servidores oficiais da Valve.

---

## ⌨️ Comandos e Atalhos no Console

* `exec my_bot_normal_config.cfg` — Carrega todas as configurações competitivas SS+.
* `bot_ss_plus` — Ativa o perfil competitivo padrão SS+ (`bot_aim mixed`, `bot_nades normal`, `mp_c4timer 40`).
* `bot_hardcore_hs` — Modo treino de *headshot* extremo (`bot_aim head`).
* `bot_aim mixed` / `bot_aim head` / `bot_aim body` — Alterna a distribuição de alvos do `BotAimImprover`.
* `bot_nades normal` — Ativa o sistema de utilitários táticos (`NadeSystem`).
* `!ws` (no chat) — Atualiza skins e inventário do `InventorySimulator`.
