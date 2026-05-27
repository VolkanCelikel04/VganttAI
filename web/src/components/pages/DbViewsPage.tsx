"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";

import { adminApi } from "@/services/adminApi";
import type { AdminRelation, AdminView, SaveAdminViewInput } from "@/types/admin";
import { Modal } from "@/components/ui/Modal";
import { StatusMessage } from "@/components/ui/StatusMessage";

type DbViewsPageProps = {
  token: string;
};

type ViewFormState = {
  viewId?: string | null;
  viewName: string;
  displayNameTr: string;
  descriptionTr: string;
  isActive: boolean;
};

const emptyForm: ViewFormState = {
  viewId: null,
  viewName: "",
  displayNameTr: "",
  descriptionTr: "",
  isActive: true
};

export function DbViewsPage({ token }: DbViewsPageProps) {
  const [views, setViews] = useState<AdminView[]>([]);
  const [relations, setRelations] = useState<AdminRelation[]>([]);
  const [form, setForm] = useState<ViewFormState>(emptyForm);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "error" | "warning"; text: string } | null>(null);

  const relationUsage = useMemo(() => {
    const used = new Set<string>();
    for (const relation of relations) {
      used.add(relation.sourceViewId);
      used.add(relation.targetViewId);
    }
    return used;
  }, [relations]);

  async function loadData() {
    setIsLoading(true);
    setMessage(null);
    try {
      const [nextViews, nextRelations] = await Promise.all([
        adminApi.listViews(token),
        adminApi.listRelations(token)
      ]);
      setViews(nextViews);
      setRelations(nextRelations);
    } catch (error) {
      setMessage({ tone: "error", text: getErrorText(error) });
    } finally {
      setIsLoading(false);
    }
  }

  useEffect(() => {
    void loadData();
  }, [token]);

  function openCreateModal() {
    setForm(emptyForm);
    setMessage(null);
    setIsModalOpen(true);
  }

  function openEditModal(view: AdminView) {
    setForm({
      viewId: view.viewId,
      viewName: view.viewName,
      displayNameTr: view.displayNameTr,
      descriptionTr: view.descriptionTr,
      isActive: view.isActive
    });
    setMessage(null);
    setIsModalOpen(true);
  }

  async function saveView(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!form.viewName.trim()) {
      setMessage({ tone: "error", text: "View Adı boş bırakılamaz." });
      return;
    }

    const payload: SaveAdminViewInput = {
      viewId: form.viewId || null,
      viewName: form.viewName.trim(),
      displayNameTr: form.displayNameTr.trim(),
      descriptionTr: normalizeKeywords(form.descriptionTr),
      isActive: form.isActive
    };

    setIsSaving(true);
    try {
      const saved = payload.viewId
        ? await adminApi.updateView(token, payload)
        : await adminApi.createView(token, payload);
      setIsModalOpen(false);
      setMessage({ tone: "success", text: `${saved.viewName} kaydedildi.` });
      await loadData();
    } catch (error) {
      setMessage({ tone: "error", text: getErrorText(error) });
    } finally {
      setIsSaving(false);
    }
  }

  async function deleteView(view: AdminView) {
    if (relationUsage.has(view.viewId)) {
      setMessage({
        tone: "warning",
        text: "Bu view relation içinde kullanıldığı için silinemez. Önce ilgili relation kayıtlarını kaldırın."
      });
      return;
    }

    if (!window.confirm(`${view.viewName} silinsin mi?`)) {
      return;
    }

    setIsSaving(true);
    try {
      await adminApi.deleteView(token, view.viewId);
      setMessage({ tone: "success", text: `${view.viewName} silindi.` });
      await loadData();
    } catch (error) {
      setMessage({ tone: "error", text: getErrorText(error) });
    } finally {
      setIsSaving(false);
    }
  }

  return (
    <section className="page-stack">
      <div className="page-heading">
        <div>
          <h1>DB Views</h1>
          <p>Yapay zekanın kullanacağı PostgreSQL view tanımlarını yönetin.</p>
        </div>
        <button className="primary-button" type="button" onClick={openCreateModal}>
          + View Ekle
        </button>
      </div>

      <StatusMessage tone={message?.tone}>{message?.text}</StatusMessage>

      <div className="data-panel">
        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>No</th>
                <th>View Adı</th>
                <th>View Tanımı</th>
                <th>View Detay Kelimeleri</th>
                <th>Aktif</th>
                <th>İşlem</th>
              </tr>
            </thead>
            <tbody>
              {isLoading ? (
                <tr>
                  <td colSpan={6}>Yükleniyor...</td>
                </tr>
              ) : views.length === 0 ? (
                <tr>
                  <td colSpan={6}>Kayıt bulunamadı.</td>
                </tr>
              ) : (
                views.map((view, index) => (
                  <tr key={view.viewId}>
                    <td>{index + 1}</td>
                    <td className="mono-cell">{view.viewName}</td>
                    <td>{view.displayNameTr || "-"}</td>
                    <td>{view.descriptionTr || "-"}</td>
                    <td>
                      <span className={view.isActive ? "badge badge--active" : "badge"}>
                        {view.isActive ? "Aktif" : "Pasif"}
                      </span>
                    </td>
                    <td>
                      <div className="action-row">
                        <button className="secondary-button" type="button" onClick={() => openEditModal(view)}>
                          Düzenle
                        </button>
                        <button className="danger-button" disabled={isSaving} type="button" onClick={() => deleteView(view)}>
                          Sil
                        </button>
                      </div>
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <Modal
        open={isModalOpen}
        title={form.viewId ? "View Düzenle" : "View Ekle"}
        onClose={() => setIsModalOpen(false)}
      >
        <form className="modal-form" onSubmit={saveView}>
          <label>
            View Adı
            <input
              required
              value={form.viewName}
              onChange={(event) => setForm((current) => ({ ...current, viewName: event.target.value }))}
            />
          </label>
          <label>
            View Tanımı
            <input
              value={form.displayNameTr}
              onChange={(event) => setForm((current) => ({ ...current, displayNameTr: event.target.value }))}
            />
          </label>
          <label>
            View Detay Kelimeleri
            <textarea
              placeholder="sipariş, müşteri, fatura"
              value={form.descriptionTr}
              onChange={(event) => setForm((current) => ({ ...current, descriptionTr: event.target.value }))}
            />
          </label>
          <label className="checkbox-line">
            <input
              checked={form.isActive}
              type="checkbox"
              onChange={(event) => setForm((current) => ({ ...current, isActive: event.target.checked }))}
            />
            Aktif
          </label>
          <div className="modal-actions">
            <button className="secondary-button" type="button" onClick={() => setIsModalOpen(false)}>
              Vazgeç
            </button>
            <button className="primary-button" disabled={isSaving} type="submit">
              {isSaving ? "Kaydediliyor..." : "Kaydet"}
            </button>
          </div>
        </form>
      </Modal>
    </section>
  );
}

function normalizeKeywords(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .join(", ");
}

function getErrorText(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}
