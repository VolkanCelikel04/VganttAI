"use client";

import { FormEvent, useEffect, useState } from "react";

import { AdminLayout, type AdminSection } from "@/components/layout/AdminLayout";
import { DbViewColumnsPage } from "@/components/pages/DbViewColumnsPage";
import { DbViewRelationsPage } from "@/components/pages/DbViewRelationsPage";
import { DbViewsPage } from "@/components/pages/DbViewsPage";
import { UserSettingsPage } from "@/components/pages/UserSettingsPage";
import { StatusMessage } from "@/components/ui/StatusMessage";
import { adminApi } from "@/services/adminApi";
import type { AdminUser } from "@/types/admin";

const tokenStorageKey = "vgantt_admin_token";

export function AdminApp() {
  const [activeSection, setActiveSection] = useState<AdminSection>("views");
  const [token, setToken] = useState<string | null>(null);
  const [user, setUser] = useState<AdminUser | null>(null);
  const [isBooting, setIsBooting] = useState(true);
  const [loginError, setLoginError] = useState("");

  useEffect(() => {
    async function boot() {
      const storedToken = readStoredToken();
      if (!storedToken) {
        setIsBooting(false);
        return;
      }

      try {
        const me = await adminApi.me(storedToken);
        setToken(storedToken);
        setUser(me);
      } catch {
        removeStoredToken();
      } finally {
        setIsBooting(false);
      }
    }

    void boot();
  }, []);

  async function login(username: string, password: string) {
    setLoginError("");
    const result = await adminApi.login(username, password);
    const nextToken = result.token;
    writeStoredToken(nextToken);
    setToken(nextToken);
    setUser({
      userDisplayName: result.userDisplayName,
      tenantName: result.tenantName,
      tenantCode: "",
      role: result.role
    });

    try {
      setUser(await adminApi.me(nextToken));
    } catch {
      // The login response already has enough profile data for the shell.
    }
  }

  function logout() {
    removeStoredToken();
    setToken(null);
    setUser(null);
    setActiveSection("views");
  }

  if (isBooting) {
    return (
      <div className="boot-screen">
        <span className="loader" />
        Admin panel hazırlanıyor...
      </div>
    );
  }

  if (!token || !user) {
    return <LoginScreen error={loginError} onError={setLoginError} onLogin={login} />;
  }

  return (
    <AdminLayout
      activeSection={activeSection}
      user={user}
      onLogout={logout}
      onSectionChange={setActiveSection}
    >
      {activeSection === "views" && <DbViewsPage token={token} />}
      {activeSection === "columns" && <DbViewColumnsPage token={token} />}
      {activeSection === "relations" && <DbViewRelationsPage token={token} />}
      {activeSection === "settings" && <UserSettingsPage user={user} onLogout={logout} />}
    </AdminLayout>
  );
}

function readStoredToken() {
  try {
    return window.localStorage.getItem(tokenStorageKey);
  } catch {
    return null;
  }
}

function writeStoredToken(token: string) {
  try {
    window.localStorage.setItem(tokenStorageKey, token);
  } catch {
    // Private or embedded browsers may block storage; the in-memory session still works.
  }
}

function removeStoredToken() {
  try {
    window.localStorage.removeItem(tokenStorageKey);
  } catch {
    // Storage may be unavailable in embedded browser contexts.
  }
}

type LoginScreenProps = {
  error: string;
  onError: (message: string) => void;
  onLogin: (username: string, password: string) => Promise<void>;
};

function LoginScreen({ error, onError, onLogin }: LoginScreenProps) {
  const [username, setUsername] = useState("volkan.celikel@vgantt.com");
  const [password, setPassword] = useState("");
  const [isLoading, setIsLoading] = useState(false);

  async function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!username.trim() || !password) {
      onError("Kullanıcı adı ve şifre zorunludur.");
      return;
    }

    setIsLoading(true);
    try {
      await onLogin(username.trim(), password);
    } catch (loginError) {
      onError(loginError instanceof Error ? loginError.message : String(loginError));
    } finally {
      setIsLoading(false);
    }
  }

  return (
    <main className="login-screen">
      <section className="login-panel">
        <div className="login-brand">
          <span className="logo-mark logo-mark--large" aria-hidden="true">
            VG
          </span>
          <div>
            <h1>VGanttAI Admin</h1>
            <p>PostgreSQL view ve relation yönetimi</p>
          </div>
        </div>

        <form className="login-form" onSubmit={handleSubmit}>
          <label>
            Kullanıcı adı
            <input
              autoComplete="username"
              value={username}
              onChange={(event) => setUsername(event.target.value)}
            />
          </label>
          <label>
            Şifre
            <input
              autoComplete="current-password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
            />
          </label>
          <StatusMessage tone="error">{error}</StatusMessage>
          <button className="primary-button primary-button--full" disabled={isLoading} type="submit">
            {isLoading ? "Giriş yapılıyor..." : "Giriş yap"}
          </button>
        </form>
      </section>
    </main>
  );
}
