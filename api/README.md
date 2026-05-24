# Vgantt ERP AI Backend

Backend mobil uygulama ile PostgreSQL tenant veritabanlari arasindaki tek guvenli katman olacak.

Planlanan endpointler:

- `POST /auth/login`: Kullanici dogrular, JWT dondurur.
- `GET /tenant/schema`: Aktif tenant icin tablo ve kolon metadata bilgisini dondurur.
- `POST /assistant/ask`: Soruyu SQL'e cevirir, yalnizca guvenli `SELECT` calistirir.

Guvenlik notlari:

- Mobil uygulama musteri DB baglanti bilgisini asla almamalidir.
- Tenant DB kullanicilari read-only olmalidir.
- SQL validator `SELECT` disindaki tum komutlari reddetmelidir.
- Sorgulara zorunlu limit ve audit log eklenmelidir.
