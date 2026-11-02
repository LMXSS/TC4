#!/usr/bin/env bash
# Cria (ou atualiza) todos os Secrets do Kubernetes da plataforma FCG.
#
# Os valores NUNCA ficam em arquivo versionado: chegam por variavel de ambiente — na
# pipeline, a partir dos GitHub Secrets do repositorio fcg-platform; localmente, do
# create-secrets.ps1, que gera senhas aleatorias. O script monta os Secrets com
# "kubectl create secret --dry-run=client | kubectl apply", que e idempotente: rodar de
# novo apenas atualiza os valores.
#
# Variaveis obrigatorias:
#   JWT_KEY              chave HS256 do JWT (>= 32 caracteres) — Users, Catalog e Kong
#   POSTGRES_PASSWORD    senha do usuario fcg no PostgreSQL
#   MONGO_PASSWORD       senha do usuario fcg (root) no MongoDB
#   REDIS_PASSWORD       senha do Redis (requirepass)
#   RABBITMQ_PASSWORD    senha do usuario fcg no RabbitMQ
#   ELASTIC_PASSWORD     senha do usuario elastic no Elasticsearch
#   GRAFANA_PASSWORD     senha do admin do Grafana
#   SEED_ADMIN_PASSWORD  senha do administrador inicial da FCG (admin@fcg.local)
#   AZURITE_ACCOUNT_KEY  chave (base64) da conta de storage do host de Functions
#
# Uso: ./k8s/secrets/create-secrets.sh            (namespace atual do kubectl)

set -euo pipefail

obrigatorias=(JWT_KEY POSTGRES_PASSWORD MONGO_PASSWORD REDIS_PASSWORD RABBITMQ_PASSWORD
              ELASTIC_PASSWORD GRAFANA_PASSWORD SEED_ADMIN_PASSWORD AZURITE_ACCOUNT_KEY)
faltando=()
for v in "${obrigatorias[@]}"; do
  [[ -z "${!v:-}" ]] && faltando+=("$v")
done
if (( ${#faltando[@]} > 0 )); then
  echo "Variaveis ausentes: ${faltando[*]}" >&2
  exit 1
fi

if (( ${#JWT_KEY} < 32 )); then
  echo "JWT_KEY precisa de pelo menos 32 caracteres." >&2
  exit 1
fi

# Usuarios nao sao segredo, mas ficam junto das senhas para que cada servico leia
# credencial completa de um unico lugar.
PG_USER=fcg
MONGO_USER=fcg
RABBIT_USER=fcg
AZURITE_ACCOUNT=fcgstorage

aplicar() {
  local nome="$1"; shift
  kubectl create secret generic "$nome" "$@" --dry-run=client -o yaml | kubectl apply -f -
}

# Escapa valores para dentro de connection strings (aspas simples no Npgsql).
pg_conn() {
  local db="$1"
  printf "Host=postgres;Port=5432;Database=%s;Username=%s;Password='%s'" "$db" "$PG_USER" "${POSTGRES_PASSWORD//\'/\'\'}"
}

urlencode() {
  python3 -c 'import sys, urllib.parse; print(urllib.parse.quote(sys.argv[1], safe=""))' "$1"
}

MONGO_CONN="mongodb://${MONGO_USER}:$(urlencode "$MONGO_PASSWORD")@mongo:27017/?authSource=admin"
REDIS_CONN="redis:6379,password=${REDIS_PASSWORD},abortConnect=false"
RABBIT_AMQP="amqp://${RABBIT_USER}:$(urlencode "$RABBITMQ_PASSWORD")@rabbitmq:5672/"
AZURITE_CONN="DefaultEndpointsProtocol=http;AccountName=${AZURITE_ACCOUNT};AccountKey=${AZURITE_ACCOUNT_KEY};BlobEndpoint=http://azurite:10000/${AZURITE_ACCOUNT};QueueEndpoint=http://azurite:10001/${AZURITE_ACCOUNT};TableEndpoint=http://azurite:10002/${AZURITE_ACCOUNT}"

echo ">> Infraestrutura"
aplicar postgres-secret      --from-literal=username="$PG_USER"     --from-literal=password="$POSTGRES_PASSWORD"
aplicar mongo-secret         --from-literal=username="$MONGO_USER"  --from-literal=password="$MONGO_PASSWORD"
aplicar redis-secret         --from-literal=password="$REDIS_PASSWORD"
aplicar rabbitmq-secret      --from-literal=username="$RABBIT_USER" --from-literal=password="$RABBITMQ_PASSWORD"
aplicar elasticsearch-secret --from-literal=password="$ELASTIC_PASSWORD"
aplicar azurite-secret       --from-literal=accounts="${AZURITE_ACCOUNT}:${AZURITE_ACCOUNT_KEY}"
aplicar grafana-secret       --from-literal=admin-user=admin        --from-literal=admin-password="$GRAFANA_PASSWORD"
aplicar kong-secret          --from-literal=fcg-jwt-key="$JWT_KEY"

echo ">> Microsservicos"
aplicar users-api-secret \
  --from-literal=jwt-key="$JWT_KEY" \
  --from-literal=connection-string="$(pg_conn fcg_users)" \
  --from-literal=rabbitmq-username="$RABBIT_USER" \
  --from-literal=rabbitmq-password="$RABBITMQ_PASSWORD" \
  --from-literal=seed-admin-password="$SEED_ADMIN_PASSWORD"

aplicar catalog-api-secret \
  --from-literal=jwt-key="$JWT_KEY" \
  --from-literal=connection-string="$(pg_conn fcg_catalog)" \
  --from-literal=rabbitmq-username="$RABBIT_USER" \
  --from-literal=rabbitmq-password="$RABBITMQ_PASSWORD" \
  --from-literal=mongo-connection-string="$MONGO_CONN" \
  --from-literal=redis-connection-string="$REDIS_CONN" \
  --from-literal=elasticsearch-username=elastic \
  --from-literal=elasticsearch-password="$ELASTIC_PASSWORD"

aplicar payments-api-secret \
  --from-literal=connection-string="$(pg_conn fcg_payments)" \
  --from-literal=rabbitmq-username="$RABBIT_USER" \
  --from-literal=rabbitmq-password="$RABBITMQ_PASSWORD"

aplicar notifications-function-secret \
  --from-literal=rabbitmq-connection="$RABBIT_AMQP" \
  --from-literal=azure-webjobs-storage="$AZURITE_CONN"

echo "Secrets aplicados."
