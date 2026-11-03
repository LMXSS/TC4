# Roteiro do vídeo "Pitch Final" — Tech Challenge Fase 4

**Duração alvo: 22 minutos** (limite de 25). Cada bloco diz o que falar e o que mostrar. Os quatro itens obrigatórios do enunciado estão marcados com ★.

| Bloco | Tema | Tempo |
|---|---|---|
| 1 | Abertura: os três gargalos | 1:30 |
| 2 | ★ Infraestrutura na Azure (portal + pods) | 4:00 |
| 3 | ★ Busca avançada com Elasticsearch | 4:00 |
| 4 | ★ NoSQL na arquitetura e ★ impacto do cache | 4:00 |
| 5 | ★ Live Deploy pela pipeline | 6:00 |
| 6 | Segurança: zero credenciais | 2:00 |
| 7 | Fechamento | 0:30 |

---

## Antes de gravar

1. Cluster de pé e pipelines verdes (`setup-azure.ps1` rodado, **Platform deploy** e as quatro **CI/CD** executadas ao menos uma vez).
2. Se o cluster estava parado: `az aks start --resource-group rg-fcg --name fcg-aks` (≈ 5 min) e confira `kubectl get pods`.
3. Num terminal PowerShell, na pasta `TC4\fcg-platform`, prepare as variáveis:

```powershell
$GW = "http://$(kubectl get svc kong-proxy -o jsonpath='{.status.loadBalancer.ingress[0].ip}')"
$senhas = Get-Content .\infra\azure\.secrets.local.json | ConvertFrom-Json
$login = Invoke-RestMethod "$GW/api/auth/login" -Method Post -ContentType 'application/json' `
  -Body (@{ email = "admin@fcg.local"; password = $senhas.SEED_ADMIN_PASSWORD } | ConvertTo-Json)
$admin = @{ Authorization = "Bearer $($login.token)" }
```

4. Cadastre alguns jogos para a busca ter o que mostrar (rode uma vez antes; no vídeo você cadastra só o último):

```powershell
$jogos = @(
  @{ titulo = "Hollow Knight";        genero = "Metroidvania";  preco = 46.99;  desc = "Metroidvania desenhado a mao com combate dificil";            tags = @("metroidvania","dificil","indie") },
  @{ titulo = "Hades";                genero = "Roguelike";     preco = 73.99;  desc = "Roguelike de acao na mitologia grega";                         tags = @("roguelike","acao","mitologia") },
  @{ titulo = "Stardew Valley";       genero = "Simulacao";     preco = 24.99;  desc = "Simulacao de fazenda relaxante com multiplayer cooperativo";   tags = @("fazenda","relaxante","cooperativo") },
  @{ titulo = "The Witcher 3";        genero = "RPG de acao";   preco = 79.99;  desc = "RPG de acao em mundo aberto com historia madura";              tags = @("rpg","mundo-aberto","fantasia") },
  @{ titulo = "Minecraft";            genero = "Sandbox";       preco = 99.00;  desc = "Sandbox de construcao e sobrevivencia em mundo aberto";        tags = @("sandbox","construcao","sobrevivencia") }
)
foreach ($j in $jogos) {
  $novo = Invoke-RestMethod "$GW/api/games" -Method Post -Headers $admin -ContentType 'application/json' `
    -Body (@{ titulo = $j.titulo; genero = $j.genero; preco = $j.preco } | ConvertTo-Json)
  Invoke-RestMethod "$GW/api/games/$($novo.id)/details" -Method Put -Headers $admin -ContentType 'application/json' `
    -Body (@{ descricao = $j.desc; tags = $j.tags; plataformas = @("PC") } | ConvertTo-Json) | Out-Null
}
```

5. Abas abertas: portal da Azure no resource group `rg-fcg`; GitHub do `LMXSS/TC4` na aba **Actions**; VS Code com o repositório TC4 aberto; Grafana (`kubectl port-forward svc/grafana 3000:3000`, senha em `$senhas.GRAFANA_PASSWORD`).

---

## Bloco 1 — Abertura (1:30)

**Falar.** A FCG terminou a Fase 3 distribuída e observável, mas rodando num Kubernetes local. Para virar produto de verdade faltavam três coisas: um ambiente de produção gerenciado, entrega automatizada sem passo manual, e performance — a busca dependia de um índice de texto simples e não perdoava erro de digitação. Nesta fase levamos a plataforma para o AKS, automatizamos o ciclo de vida com GitHub Actions e adicionamos Elasticsearch à persistência poliglota. E tiramos toda credencial do código.

**Mostrar.** O diagrama do README do `fcg-platform` (seção *Arquitetura*) e a tabela *O que mudou na Fase 4*.

---

## Bloco 2 — ★ Infraestrutura na Azure (4:00)

**Falar.** Toda a infra é código. O Bicep cria o registry privado, o cluster gerenciado e o monitoramento, e dá aos nós do AKS permissão de puxar imagens do ACR pela identidade gerenciada — não existe senha de registry.

**Mostrar.**
1. `infra/azure/main.bicep` rapidamente: ACR com `adminUserEnabled: false`, AKS, role `AcrPull`.
2. **Portal → `rg-fcg`**: os recursos (AKS, ACR, Log Analytics, e o resource group `MC_` com VMs, discos, Load Balancer e IP público).
3. **AKS → Node pools**: 2 nós B2ms. **Workloads**: os Deployments e StatefulSets. **Insights**: CPU e memória por nó.
4. **ACR → Repositories → catalog-api**: as tags `<run>-<sha7>`, uma por commit.
5. No terminal:

```powershell
kubectl get nodes -o wide
kubectl get pods -o wide          # réplicas espalhadas pelos dois nós
kubectl get svc kong-proxy        # EXTERNAL-IP do Azure Load Balancer
kubectl get hpa
```

**Falar.** O único ponto exposto à internet é o Kong, por um Service do tipo LoadBalancer — o AKS criou o Azure Load Balancer e o IP público sozinho. Banco relacional e Elasticsearch são StatefulSets com Azure Disk; as APIs escalam por HPA. Trocamos o SQLite por PostgreSQL: com SQLite dentro do pod, cada réplica tinha um banco diferente e o rolling update apagava os dados.

---

## Bloco 3 — ★ Busca avançada com Elasticsearch (4:00)

**Falar.** A busca agora é um motor independente do banco relacional. Toda vez que um jogo é cadastrado ou editado, a Catalog API atualiza o índice na mesma requisição.

**Mostrar.**

```powershell
# Cadastro: PostgreSQL → MongoDB → Elasticsearch
$jogo = Invoke-RestMethod "$GW/api/games" -Method Post -Headers $admin -ContentType 'application/json' `
  -Body '{"titulo":"Elden Ring","genero":"RPG de acao","preco":249.90}'
Invoke-RestMethod "$GW/api/games/$($jogo.id)/details" -Method Put -Headers $admin -ContentType 'application/json' `
  -Body '{"descricao":"RPG de acao em mundo aberto nas Terras Intermedias","desenvolvedora":"FromSoftware","tags":["souls-like","mundo-aberto","rpg"],"plataformas":["PC","PS5"]}' | Out-Null

# Fuzzy: dois erros de digitação
$r = Invoke-RestMethod "$GW/api/games/search?q=eldn%20rign"
$r | Select-Object motor, total, tempoMs, doCache
$r.itens | Select-Object titulo, relevancia, @{n='destaque';e={$_.destaques.titulo}}
```

**Falar.** "eldn rign" encontrou *Elden Ring*. A resposta mostra a relevância de cada jogo e os trechos que casaram.

```powershell
# A busca da Fase 3 (MongoDB) com o mesmo erro
(Invoke-RestMethod "$GW/api/games/search/basic?q=eldn%20rign").total     # 0

# Relevância: "mundo aberto" casa com vários jogos, ordenados pela nota
(Invoke-RestMethod "$GW/api/games/search?q=mundo%20aberto").itens | Select-Object titulo, relevancia

# Sem acento e com erro, mais filtro de preço
(Invoke-RestMethod "$GW/api/games/search?q=acao%20mitolgia&precoMaximo=100").itens | Select-Object titulo, preco, relevancia
```

**Mostrar no código.** `ElasticQueryBuilder.SearchBody`: o `multi_match` com `fuzziness: AUTO` e os pesos, o `match_phrase` no título, o `sort` por `_score`, e os filtros em `filter`. Depois o `GameSearchIndexer` chamado pelo `GameDetailsService` em toda escrita.

**Falar.** Se o Elasticsearch cair, a busca não quebra: responde pelo MongoDB e o campo `motor` mostra isso. E na volta, o índice se reconstrói sozinho no start ou pelo endpoint de reindex.

---

## Bloco 4 — ★ NoSQL e ★ cache (4:00)

**Falar — NoSQL.** O MongoDB guarda o que não cabe bem no relacional: o catálogo expandido, com esquema diferente para cada jogo, e as avaliações, que têm alto volume e precisam de agregação. O PostgreSQL continua sendo a fonte da verdade do jogo; título, gênero e preço são denormalizados no documento, e o índice do Elasticsearch junta os dois.

**Mostrar.**

```powershell
$d = Invoke-RestMethod "$GW/api/games/$($jogo.id)/details"
$d | Select-Object titulo, desenvolvedora, tags, plataformas
$d.avaliacoes
```

Mostrar `GameDetailsDocument` (campos livres, `Dictionary` de requisitos) e o pipeline de agregação em `ReviewService`.

**Falar — cache.** Na frente das consultas caras fica o Redis. Vamos medir.

```powershell
# Documento expandido: MongoDB + agregação de notas na 1ª; Redis nas seguintes
1..5 | ForEach-Object { "{0,6:N1} ms" -f (Measure-Command { Invoke-RestMethod "$GW/api/games/$($jogo.id)/details" }).TotalMilliseconds }

# Busca: a 2ª igual sai do Redis, sem tocar o Elasticsearch
(Invoke-RestMethod "$GW/api/games/search?q=hollow%20night").doCache   # False
(Invoke-RestMethod "$GW/api/games/search?q=hollow%20night").doCache   # True
```

**Falar.** A primeira chamada paga o MongoDB e a agregação; as seguintes saem do Redis. E qualquer escrita no catálogo troca o token de versão das buscas, então o cache nunca devolve resultado velho.

**Mostrar.** Gere carga e abra o Grafana:

```powershell
1..200 | ForEach-Object {
  Invoke-RestMethod "$GW/api/games/$($jogo.id)/details" | Out-Null
  Invoke-RestMethod "$GW/api/games/search?q=mundo%20aberto" | Out-Null
}
```

Painéis: **Acerto do cache (Redis)**, **Cache: acertos × faltas**, e o bloco novo **Busca avançada** (buscas por motor, duração p95 no Elasticsearch, indexações).

---

## Bloco 5 — ★ Live Deploy (6:00)

**Falar.** Agora o ponto principal: o código na `main` é o que está em produção. Vou alterar a Catalog API, dar push e acompanhar a pipeline atualizar o cluster sem derrubar nenhuma requisição.

**Mostrar.**

1. Num **segundo terminal**, deixe rodando um monitor que chama a API a cada meio segundo:

```powershell
$GW = "http://$(kubectl get svc kong-proxy -o jsonpath='{.status.loadBalancer.ingress[0].ip}')"
while ($true) {
  try   { $i = Invoke-RestMethod "$GW/api/games/info" -TimeoutSec 3; "{0:HH:mm:ss}  v{1}  {2}" -f (Get-Date), $i.versao, $i.pod }
  catch { (Get-Date -Format "HH:mm:ss") + "  ERRO  " + $_.Exception.Message }
  Start-Sleep -Milliseconds 500
}
```

2. Num **terceiro terminal**: `kubectl get pods -l app=catalog-api -w`

3. No VS Code, em `fcg-catalog-api/Catalog.Api/Controllers/InfoController.cs`, troque a versão:

```csharp
public const string Versao = "4.1.0";
```

4. Commit e push:

```powershell
git add fcg-catalog-api/Catalog.Api/Controllers/InfoController.cs
git commit -m "feat: catalog-api 4.1.0 (live deploy)"
git push
```

5. GitHub → **Actions**: aparece só a execução **CI/CD - Catalog API** (o filtro de pasta não dispara as outras pipelines). Abra e narre cada job enquanto roda:
   * **Build & Test** — compila e roda os testes unitários (mostre o passo de testes com a contagem);
   * **Imagem Docker** — gera a imagem com a tag `<run>-<sha7>`, passa o Trivy (mostre a tabela do scan) e publica no ACR via OIDC;
   * **Deploy no AKS** — `kustomize edit set image` e `kubectl apply -k`, depois `rollout status`.

6. Durante o deploy, volte aos terminais:
   * no `-w`, um pod novo nasce, fica `Ready`, e só então um antigo termina — um de cada vez;
   * no monitor, as respostas alternam entre `v4.0.0` e `v4.1.0` (pods diferentes) e terminam todas em `v4.1.0`, **sem nenhuma linha de ERRO**.

7. Feche mostrando o **resumo da execução** (imagem publicada, pods, histórico de revisões) e:

```powershell
kubectl rollout history deployment/catalog-api
kubectl get deployment catalog-api -o jsonpath='{.spec.template.spec.containers[0].image}'
```

**Falar.** O rolling update usa `maxUnavailable: 0`: nenhum pod antigo sai antes de um novo passar no readiness. O `preStop` tira o pod do balanceamento antes de ele parar. E se a versão nova não ficasse saudável em cinco minutos, a própria pipeline faria o rollback.

> **Se der tempo:** abra um pull request com qualquer mudança e mostre que nele rodam build, testes e scan, mas o job de deploy é pulado.

---

## Bloco 6 — Segurança: zero credenciais (2:00)

**Falar.** Não existe nenhuma senha, chave ou connection string no código nem nos YAML.

**Mostrar.**
1. `fcg-catalog-api/k8s/deployment.yaml`: as variáveis sensíveis vêm todas de `secretKeyRef`; não há `secret.yaml` no repositório.
2. `appsettings.json` sem `Jwt:Key` e sem senha.
3. GitHub → `LMXSS/TC4` → **Settings → Secrets and variables → Actions**: os segredos (valores ocultos).
4. `k8s/secrets/create-secrets.sh`: lê das variáveis de ambiente e aplica os Secrets no cluster.
5. A pipeline do platform reprova se algum manifesto tiver `kind: Secret`.
6. `azure/login` com OIDC: nenhuma credencial da Azure guardada — a Azure confia no token que o GitHub emite para aquele repositório e branch.

```powershell
kubectl get secrets
kubectl describe secret catalog-api-secret     # mostra as chaves, nunca os valores
```

---

## Bloco 7 — Fechamento (0:30)

**Falar.** A FCG agora roda num Kubernetes gerenciado na Azure, com imagens privadas, deploy contínuo sem downtime a cada push, busca tolerante a erro com Elasticsearch, cache no Redis, MongoDB para os dados flexíveis, PostgreSQL compartilhado entre as réplicas, e nenhuma credencial fora do lugar. Obrigado.

---

## Depois de gravar

```powershell
# Volte a versão (ou mantenha 4.1.0 — a main reflete produção de qualquer forma)
az aks stop --resource-group rg-fcg --name fcg-aks     # para de cobrar pelos nós
```
