# Vgantt ERP AI Assistant

Flutter mobil uygulama ve ileride eklenecek backend API icin baslangic workspace'i.

## Dizinler

- `mobile`: Flutter Android/iOS uygulamasi.
- `api`: Backend API taslak notlari.
- `database`: Merkez tenant registry PostgreSQL semasi.
- `tools/flutter`: Projeye lokal kurulan Flutter SDK.

## Flutter

Bu workspace sandbox uyumlu lokal Flutter config kullanir:

```bash
./scripts/flutter.sh --version
./scripts/flutter.sh run
```

Uygulama varsayilan olarak production API'ye baglanir:

```bash
./scripts/flutter.sh run
```

Varsayilan API adresi:

```text
https://api.vgantt.com
```

Lokal API veya mock backend ile calistirmak icin:

```bash
./scripts/flutter.sh run --dart-define=API_BASE_URL=http://127.0.0.1:5055
./scripts/flutter.sh run --dart-define=MOCK_BACKEND=true
```

## API

Backend PostgreSQL baglantisini ortam degiskeninden okur. Production'da API ve PostgreSQL ayni Hetzner sunucusunda konumlanir; PostgreSQL disariya acilmaz, API `localhost:5432` uzerinden baglanir.

Sunucuda `api/.env` icin beklenen ana deger:

```bash
VGANTT_REGISTRY_CONNECTION="Host=localhost;Port=5432;Database=vgantt_db;Username=vganttuser;Password=DB_PASSWORD;SSL Mode=Disable"
```

Migration ve ilk admin kullanicisi:

```bash
cp api/.env.example api/.env
./scripts/api.sh --migrate
./scripts/api.sh --seed-admin
./scripts/api.sh
```

Kontrol:

```bash
curl http://127.0.0.1:5055/health
curl http://127.0.0.1:5055/db/health
```
