"use client";

import type { AdminUser } from "@/types/admin";

type UserSettingsPageProps = {
  onLogout: () => void;
  user: AdminUser;
};

export function UserSettingsPage({ onLogout, user }: UserSettingsPageProps) {
  return (
    <section className="page-stack">
      <div className="page-heading">
        <div>
          <h1>Kullanıcı Ayarları</h1>
          <p>Oturum ve şirket bilgilerini görüntüleyin.</p>
        </div>
      </div>

      <div className="settings-grid">
        <section className="settings-panel">
          <h2>Profil</h2>
          <dl className="details-list">
            <div>
              <dt>Kullanıcı</dt>
              <dd>{user.userDisplayName}</dd>
            </div>
            <div>
              <dt>Rol</dt>
              <dd>{user.role}</dd>
            </div>
            <div>
              <dt>Şirket</dt>
              <dd>{user.tenantName}</dd>
            </div>
            <div>
              <dt>Şirket Kodu</dt>
              <dd>{user.tenantCode}</dd>
            </div>
          </dl>
        </section>

        <section className="settings-panel">
          <h2>Oturum</h2>
          <p>Bu cihazdaki admin oturumunu kapatabilirsiniz.</p>
          <button className="danger-button danger-button--wide" type="button" onClick={onLogout}>
            Çıkış yap
          </button>
        </section>
      </div>
    </section>
  );
}
