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

Mobil uygulama sesli soru ve sesli cevap icin ucretli bir bulut servisine dogrudan baglanmaz:

- `speech_to_text`: Cihazdaki Android/iOS konusma tanima kabiliyetini kullanir.
- `flutter_tts`: Cihazdaki Android/iOS text-to-speech motorunu kullanir.

Ses akisi:

```text
Mikrofon -> konusma metne cevrilir -> /assistant/ask -> cevap ekrana yazilir -> cevap sesli okunur
```

Telefon ayni yerel agdan bu bilgisayardaki API'ye baglanacaksa bilgisayarin IP adresini kullanin:

```bash
./scripts/flutter.sh run --dart-define=API_BASE_URL=http://192.168.1.180:5055
```

## API

Backend PostgreSQL baglantisini ortam degiskeninden okur. Yerel server kurulumunda API ve PostgreSQL bu bilgisayarda calisir; ERP baglantisi bilgisayarin VPN erisimi uzerinden yapilir.

`api/env.local` icin beklenen ana degerler:

```bash
VGANTT_REGISTRY_CONNECTION="Host=localhost;Port=5432;Database=vgantt_db;Username=vganttuser;Password=DB_PASSWORD;SSL Mode=Disable"
VGANTT_API_BIND_HOST=0.0.0.0
VGANTT_API_PORT=5055
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
