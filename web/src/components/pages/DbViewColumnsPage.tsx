"use client";

import { useEffect, useMemo, useState } from "react";

import { adminApi } from "@/services/adminApi";
import type { AdminView, AdminViewColumn, SaveAdminViewColumnInput } from "@/types/admin";
import { StatusMessage } from "@/components/ui/StatusMessage";

type DbViewColumnsPageProps = {
  token: string;
};

export function DbViewColumnsPage({ token }: DbViewColumnsPageProps) {
  const [views, setViews] = useState<AdminView[]>([]);
  const [columns, setColumns] = useState<AdminViewColumn[]>([]);
  const [selectedViewId, setSelectedViewId] = useState("");
  const [isLoadingViews, setIsLoadingViews] = useState(true);
  const [isLoadingColumns, setIsLoadingColumns] = useState(false);
  const [isSaving, setIsSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "error" | "info"; text: string } | null>(null);

  const selectedView = useMemo(
    () => views.find((view) => view.viewId === selectedViewId),
    [selectedViewId, views]
  );

  useEffect(() => {
    async function loadViews() {
      setIsLoadingViews(true);
      try {
        const nextViews = await adminApi.listViews(token);
        setViews(nextViews);
        setSelectedViewId((current) => current || nextViews[0]?.viewId || "");
      } catch (error) {
        setMessage({ tone: "error", text: getErrorText(error) });
      } finally {
        setIsLoadingViews(false);
      }
    }

    void loadViews();
  }, [token]);

  useEffect(() => {
    async function loadColumns() {
      if (!selectedViewId) {
        setColumns([]);
        return;
      }

      setIsLoadingColumns(true);
      setMessage(null);
      try {
        setColumns(await adminApi.listColumns(token, selectedViewId));
      } catch (error) {
        setMessage({ tone: "error", text: getErrorText(error) });
      } finally {
        setIsLoadingColumns(false);
      }
    }

    void loadColumns();
  }, [selectedViewId, token]);

  function updateColumnField(
    columnName: string,
    field: "displayNameTr" | "semanticMeaningTr",
    value: string
  ) {
    setColumns((current) =>
      current.map((column) =>
        column.columnName === columnName ? { ...column, [field]: value } : column
      )
    );
  }

  async function saveColumns() {
    if (!selectedViewId) {
      setMessage({ tone: "error", text: "Önce bir view seçin." });
      return;
    }

    const payload: SaveAdminViewColumnInput[] = columns.map((column) => ({
      columnName: column.columnName,
      displayNameTr: column.displayNameTr.trim(),
      dataType: column.dataType,
      semanticMeaningTr: normalizeKeywords(column.semanticMeaningTr),
      isFilterable: column.isFilterable,
      isGroupable: column.isGroupable,
      isSummable: column.isSummable,
      isActive: column.isActive
    }));

    setIsSaving(true);
    try {
      const saved = await adminApi.saveColumns(token, selectedViewId, payload);
      setMessage({
        tone: "success",
        text: `${selectedView?.viewName || "View"} için ${saved.savedCount} kolon açıklaması kaydedildi.`
      });
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
          <h1>DB Views Columns</h1>
          <p>Seçili view kolonlarının Türkçe açıklamalarını düzenleyin.</p>
        </div>
      </div>

      <div className="filter-bar">
        <label>
          View Listesi
          <select
            disabled={isLoadingViews}
            value={selectedViewId}
            onChange={(event) => setSelectedViewId(event.target.value)}
          >
            {views.map((view) => (
              <option key={view.viewId} value={view.viewId}>
                {view.viewName}
              </option>
            ))}
          </select>
        </label>
      </div>

      <StatusMessage tone={message?.tone}>{message?.text}</StatusMessage>

      <div className="data-panel data-panel--columns">
        <div className="table-scroll table-scroll--columns">
          <table className="data-table data-table--columns">
            <thead>
              <tr>
                <th>View Kolon Adı</th>
                <th>View Kolon Müşteri Tanımı</th>
                <th>View Kolon Tanımları</th>
              </tr>
            </thead>
            <tbody>
              {isLoadingColumns ? (
                <tr>
                  <td colSpan={3}>Kolonlar yükleniyor...</td>
                </tr>
              ) : columns.length === 0 ? (
                <tr>
                  <td colSpan={3}>Bu view için kolon bulunamadı.</td>
                </tr>
              ) : (
                columns.map((column, index) => (
                  <tr key={column.columnId || column.columnName}>
                    <td className="mono-cell">{column.columnName}</td>
                    <td>
                      <input
                        className="table-input"
                        placeholder="Müşterinin kullandığı Türkçe kolon adı"
                        value={column.displayNameTr}
                        onChange={(event) =>
                          updateColumnField(column.columnName, "displayNameTr", event.target.value)
                        }
                      />
                    </td>
                    <td>
                      <input
                        className="table-input table-input--keywords"
                        placeholder="sipariş, tarih, müşteri"
                        value={column.semanticMeaningTr}
                        onChange={(event) =>
                          updateColumnField(column.columnName, "semanticMeaningTr", event.target.value)
                        }
                      />
                    </td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="sticky-actions">
        <button className="primary-button" disabled={isSaving || !selectedViewId || columns.length === 0} type="button" onClick={saveColumns}>
          {isSaving ? "Kaydediliyor..." : "Kaydet"}
        </button>
      </div>
    </section>
  );
}

function getErrorText(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}

function normalizeKeywords(value: string) {
  return value
    .split(",")
    .map((item) => item.trim())
    .filter(Boolean)
    .join(", ");
}
