"use client";

import type { ReactNode } from "react";

import type { AdminUser } from "@/types/admin";

export type AdminSection = "views" | "columns" | "relations" | "settings";

const navItems: Array<{ id: AdminSection; label: string }> = [
  { id: "views", label: "DB Views" },
  { id: "columns", label: "DB Views Columns" },
  { id: "relations", label: "DB Views Relations" },
  { id: "settings", label: "Kullanıcı Ayarları" }
];

type AdminLayoutProps = {
  activeSection: AdminSection;
  children: ReactNode;
  onLogout: () => void;
  onSectionChange: (section: AdminSection) => void;
  user: AdminUser;
};

export function AdminLayout({
  activeSection,
  children,
  onLogout,
  onSectionChange,
  user
}: AdminLayoutProps) {
  return (
    <div className="admin-shell">
      <aside className="sidebar">
        <div className="sidebar__brand">
          <span className="logo-mark" aria-hidden="true">
            VG
          </span>
          <div>
            <strong>VGanttAI</strong>
            <span>Admin Console</span>
          </div>
        </div>

        <nav className="sidebar__nav" aria-label="Admin menü">
          {navItems.map((item) => (
            <button
              className={item.id === activeSection ? "sidebar__item is-active" : "sidebar__item"}
              key={item.id}
              type="button"
              onClick={() => onSectionChange(item.id)}
            >
              <span aria-hidden="true" className="sidebar__item-dot" />
              {item.label}
            </button>
          ))}
        </nav>
      </aside>

      <div className="workspace">
        <header className="top-header">
          <div className="company-lockup">
            <span className="company-logo" aria-hidden="true">
              {initials(user.tenantName)}
            </span>
            <div>
              <span className="eyebrow">Şirket</span>
              <strong>{user.tenantName}</strong>
            </div>
          </div>
          <div className="header-user">
            <span>{user.userDisplayName}</span>
            <button className="ghost-button" type="button" onClick={onLogout}>
              Çıkış
            </button>
          </div>
        </header>

        <main className="workspace__main">{children}</main>
      </div>
    </div>
  );
}

function initials(value: string) {
  const words = value
    .split(/\s+/)
    .map((word) => word.trim())
    .filter(Boolean)
    .slice(0, 2);

  if (words.length === 0) {
    return "VG";
  }

  return words.map((word) => word[0]?.toUpperCase()).join("");
}
