"use client";

import { FormEvent, useEffect, useMemo, useState } from "react";

import { adminApi } from "@/services/adminApi";
import type {
  AdminRelation,
  AdminRelationColumn,
  AdminView,
  AdminViewColumn,
  SaveAdminRelationInput
} from "@/types/admin";
import { Modal } from "@/components/ui/Modal";
import { StatusMessage } from "@/components/ui/StatusMessage";

type DbViewRelationsPageProps = {
  token: string;
};

type RelationRegionType = "view" | "relation";

type RelationFormState = {
  relationId?: string | null;
  relationName: string;
  descriptionTr: string;
  sourceRegionType: RelationRegionType;
  sourceRegionId: string;
  sourceViewId: string;
  targetRegionType: RelationRegionType;
  targetRegionId: string;
  targetViewId: string;
  joinType: string;
  isActive: boolean;
  columns: AdminRelationColumn[];
};

type RegionSelection = {
  type: RelationRegionType;
  id: string;
};

type ColumnOption = {
  value: string;
  viewId: string;
  columnName: string;
  label: string;
};

const emptyRelation: RelationFormState = {
  relationId: null,
  relationName: "",
  descriptionTr: "",
  sourceRegionType: "view",
  sourceRegionId: "",
  sourceViewId: "",
  targetRegionType: "view",
  targetRegionId: "",
  targetViewId: "",
  joinType: "INNER JOIN",
  isActive: true,
  columns: [{ sourceColumnName: "", targetColumnName: "", ordinal: 1 }]
};

const columnRefSeparator = "::";

export function DbViewRelationsPage({ token }: DbViewRelationsPageProps) {
  const [views, setViews] = useState<AdminView[]>([]);
  const [relations, setRelations] = useState<AdminRelation[]>([]);
  const [columnsByView, setColumnsByView] = useState<Record<string, AdminViewColumn[]>>({});
  const [form, setForm] = useState<RelationFormState>(emptyRelation);
  const [isModalOpen, setIsModalOpen] = useState(false);
  const [isLoading, setIsLoading] = useState(true);
  const [isSaving, setIsSaving] = useState(false);
  const [message, setMessage] = useState<{ tone: "success" | "error" | "warning"; text: string } | null>(null);

  const viewMap = useMemo(() => new Map(views.map((view) => [view.viewId, view])), [views]);
  const relationMap = useMemo(
    () => new Map(relations.map((relation) => [relation.relationId, relation])),
    [relations]
  );

  const selectableRelations = useMemo(
    () => relations.filter((relation) => relation.relationId !== form.relationId),
    [form.relationId, relations]
  );

  const defaultRegions = useMemo(() => {
    const firstView = views[0]?.viewId || "";
    const secondView = views[1]?.viewId || firstView;

    if (firstView) {
      return {
        source: { type: "view" as RelationRegionType, id: firstView },
        target: { type: "view" as RelationRegionType, id: secondView }
      };
    }

    const firstRelation = relations[0]?.relationId || "";
    return {
      source: { type: "relation" as RelationRegionType, id: firstRelation },
      target: { type: "relation" as RelationRegionType, id: firstRelation }
    };
  }, [relations, views]);

  const sourceColumnOptions = columnOptionsForRegion(form.sourceRegionType, form.sourceRegionId);
  const targetColumnOptions = columnOptionsForRegion(form.targetRegionType, form.targetRegionId);

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

  useEffect(() => {
    const viewIds = [
      ...collectRegionViewIds(form.sourceRegionType, form.sourceRegionId),
      ...collectRegionViewIds(form.targetRegionType, form.targetRegionId)
    ];

    for (const viewId of new Set(viewIds)) {
      void ensureColumns(viewId);
    }
  }, [
    form.sourceRegionType,
    form.sourceRegionId,
    form.targetRegionType,
    form.targetRegionId,
    relations,
    token
  ]);

  async function ensureColumns(viewId: string) {
    if (!viewId || columnsByView[viewId]) {
      return;
    }

    try {
      const columns = await adminApi.listColumns(token, viewId);
      setColumnsByView((current) => ({ ...current, [viewId]: columns }));
    } catch (error) {
      setMessage({ tone: "error", text: getErrorText(error) });
    }
  }

  function openCreateModal() {
    setForm({
      ...emptyRelation,
      sourceRegionType: defaultRegions.source.type,
      sourceRegionId: defaultRegions.source.id,
      sourceViewId: firstConcreteViewId(defaultRegions.source),
      targetRegionType: defaultRegions.target.type,
      targetRegionId: defaultRegions.target.id,
      targetViewId: firstConcreteViewId(defaultRegions.target)
    });
    setMessage(null);
    setIsModalOpen(true);
  }

  function openEditModal(relation: AdminRelation) {
    const sourceRegionType = relation.sourceRegionType === "relation" && relation.sourceRelationId
      ? "relation"
      : "view";
    const targetRegionType = relation.targetRegionType === "relation" && relation.targetRelationId
      ? "relation"
      : "view";

    setForm({
      relationId: relation.relationId,
      relationName: relation.relationName,
      descriptionTr: relation.descriptionTr,
      sourceRegionType,
      sourceRegionId: sourceRegionType === "relation" ? relation.sourceRelationId || "" : relation.sourceViewId,
      sourceViewId: relation.sourceViewId,
      targetRegionType,
      targetRegionId: targetRegionType === "relation" ? relation.targetRelationId || "" : relation.targetViewId,
      targetViewId: relation.targetViewId,
      joinType: relation.joinType || "INNER JOIN",
      isActive: relation.isActive,
      columns: relation.columns.length
        ? relation.columns.map((column, index) => ({
            ...column,
            sourceColumnName: makeColumnRef(relation.sourceViewId, column.sourceColumnName),
            targetColumnName: makeColumnRef(relation.targetViewId, column.targetColumnName),
            ordinal: column.ordinal || index + 1
          }))
        : [{ sourceColumnName: "", targetColumnName: "", ordinal: 1 }]
    });
    setMessage(null);
    setIsModalOpen(true);
  }

  function changeRegion(side: "source" | "target", value: string) {
    const selection = parseRegionValue(value);
    setForm((current) => {
      if (side === "source") {
        return {
          ...current,
          sourceRegionType: selection.type,
          sourceRegionId: selection.id,
          sourceViewId: firstConcreteViewId(selection),
          columns: current.columns.map((column) => ({ ...column, sourceColumnName: "" }))
        };
      }

      return {
        ...current,
        targetRegionType: selection.type,
        targetRegionId: selection.id,
        targetViewId: firstConcreteViewId(selection),
        columns: current.columns.map((column) => ({ ...column, targetColumnName: "" }))
      };
    });
  }

  function updateMatch(index: number, field: "sourceColumnName" | "targetColumnName", value: string) {
    setForm((current) => ({
      ...current,
      columns: current.columns.map((column, columnIndex) =>
        columnIndex === index ? { ...column, [field]: value } : column
      )
    }));
  }

  function addMatchRow() {
    setForm((current) => ({
      ...current,
      columns: [
        ...current.columns,
        { sourceColumnName: "", targetColumnName: "", ordinal: current.columns.length + 1 }
      ]
    }));
  }

  function removeMatchRow(index: number) {
    setForm((current) => {
      const nextColumns = current.columns.filter((_, columnIndex) => columnIndex !== index);
      return {
        ...current,
        columns: (nextColumns.length ? nextColumns : [{ sourceColumnName: "", targetColumnName: "", ordinal: 1 }]).map(
          (column, columnIndex) => ({ ...column, ordinal: columnIndex + 1 })
        )
      };
    });
  }

  async function saveRelation(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!form.relationName.trim()) {
      setMessage({ tone: "error", text: "Relation Name boş bırakılamaz." });
      return;
    }

    if (!form.sourceRegionId || !form.targetRegionId) {
      setMessage({ tone: "error", text: "Her iki bölge için view veya relation seçimi yapılmalıdır." });
      return;
    }

    const decodedColumns = form.columns
      .map((column, index) => ({
        source: parseColumnRef(column.sourceColumnName),
        target: parseColumnRef(column.targetColumnName),
        ordinal: index + 1
      }))
      .filter((column) => column.source.columnName && column.target.columnName);

    if (decodedColumns.length === 0) {
      setMessage({ tone: "error", text: "En az bir eşleşme alanı seçilmelidir." });
      return;
    }

    const sourceViewId = decodedColumns[0].source.viewId;
    const targetViewId = decodedColumns[0].target.viewId;

    if (!sourceViewId || !targetViewId) {
      setMessage({ tone: "error", text: "Eşleşme kolonları view bilgisiyle seçilmelidir." });
      return;
    }

    const hasMixedViews = decodedColumns.some(
      (column) => column.source.viewId !== sourceViewId || column.target.viewId !== targetViewId
    );
    if (hasMixedViews) {
      setMessage({
        tone: "error",
        text: "Bir relation içinde eşleşme satırları aynı 1. bölge view'u ve aynı 2. bölge view'u üzerinden seçilmelidir."
      });
      return;
    }

    const payload: SaveAdminRelationInput = {
      relationId: form.relationId || null,
      relationName: form.relationName.trim(),
      descriptionTr: form.descriptionTr.trim(),
      sourceViewId,
      targetViewId,
      sourceRegionType: form.sourceRegionType,
      sourceRelationId: form.sourceRegionType === "relation" ? form.sourceRegionId : null,
      targetRegionType: form.targetRegionType,
      targetRelationId: form.targetRegionType === "relation" ? form.targetRegionId : null,
      joinType: form.joinType,
      isActive: form.isActive,
      columns: decodedColumns.map((column) => ({
        sourceColumnName: column.source.columnName,
        targetColumnName: column.target.columnName,
        ordinal: column.ordinal
      }))
    };

    setIsSaving(true);
    try {
      const saved = payload.relationId
        ? await adminApi.updateRelation(token, payload)
        : await adminApi.createRelation(token, payload);
      setIsModalOpen(false);
      setMessage({ tone: "success", text: `${saved.relationName} kaydedildi.` });
      await loadData();
    } catch (error) {
      setMessage({ tone: "error", text: getErrorText(error) });
    } finally {
      setIsSaving(false);
    }
  }

  async function deleteRelation(relation: AdminRelation) {
    if (!window.confirm(`${relation.relationName} silinsin mi?`)) {
      return;
    }

    setIsSaving(true);
    try {
      await adminApi.deleteRelation(token, relation.relationId);
      setMessage({ tone: "success", text: `${relation.relationName} silindi.` });
      await loadData();
    } catch (error) {
      setMessage({
        tone: "warning",
        text: getErrorText(error) || "Relation başka yerde kullanıldığı için silinemedi."
      });
    } finally {
      setIsSaving(false);
    }
  }

  function collectRegionViewIds(type: RelationRegionType, id: string, visited = new Set<string>()): string[] {
    if (!id) {
      return [];
    }

    if (type === "view") {
      return [id];
    }

    if (visited.has(id)) {
      return [];
    }

    visited.add(id);
    const relation = relationMap.get(id);
    if (!relation) {
      return [];
    }

    const sourceType = relation.sourceRegionType === "relation" ? "relation" : "view";
    const sourceIds = sourceType === "relation" && relation.sourceRelationId
      ? collectRegionViewIds("relation", relation.sourceRelationId, visited)
      : [relation.sourceViewId];
    const targetType = relation.targetRegionType === "relation" ? "relation" : "view";
    const targetIds = targetType === "relation" && relation.targetRelationId
      ? collectRegionViewIds("relation", relation.targetRelationId, visited)
      : [relation.targetViewId];

    return Array.from(new Set([...sourceIds, relation.sourceViewId, ...targetIds, relation.targetViewId].filter(Boolean)));
  }

  function firstConcreteViewId(selection: RegionSelection) {
    return collectRegionViewIds(selection.type, selection.id)[0] || "";
  }

  function columnOptionsForRegion(type: RelationRegionType, id: string): ColumnOption[] {
    const viewIds = collectRegionViewIds(type, id);
    return viewIds.flatMap((viewId) => {
      const viewName = viewMap.get(viewId)?.viewName || viewId;
      return (columnsByView[viewId] || []).map((column) => ({
        value: makeColumnRef(viewId, column.columnName),
        viewId,
        columnName: column.columnName,
        label: type === "relation" ? `${viewName} / ${columnLabel(column)}` : columnLabel(column)
      }));
    });
  }

  function renderRegionOptions() {
    return (
      <>
        <option value="">View veya relation seçin</option>
        <optgroup label="View'lar">
          {views.map((view) => (
            <option key={view.viewId} value={makeRegionValue("view", view.viewId)}>
              {view.viewName}
            </option>
          ))}
        </optgroup>
        {selectableRelations.length > 0 && (
          <optgroup label="Kayıtlı Relation'lar">
            {selectableRelations.map((relation) => (
              <option key={relation.relationId} value={makeRegionValue("relation", relation.relationId)}>
                {relation.relationName} ({relation.sourceRegionName || relation.sourceViewName} - {relation.targetRegionName || relation.targetViewName})
              </option>
            ))}
          </optgroup>
        )}
      </>
    );
  }

  return (
    <section className="page-stack">
      <div className="page-heading">
        <div>
          <h1>DB Views Relations</h1>
          <p>View’lar arasındaki join ilişkilerini ve kolon eşleşmelerini yönetin.</p>
        </div>
        <button className="primary-button" type="button" onClick={openCreateModal}>
          + Relation Ekle
        </button>
      </div>

      <StatusMessage tone={message?.tone}>{message?.text}</StatusMessage>

      <div className="data-panel">
        <div className="table-scroll">
          <table className="data-table data-table--relations">
            <thead>
              <tr>
                <th>No</th>
                <th>Relation Name</th>
                <th>Relation Tanımı</th>
                <th>1. Bölge</th>
                <th>2. Bölge</th>
                <th>İşlem</th>
              </tr>
            </thead>
            <tbody>
              {isLoading ? (
                <tr>
                  <td colSpan={6}>Yükleniyor...</td>
                </tr>
              ) : relations.length === 0 ? (
                <tr>
                  <td colSpan={6}>Kayıt bulunamadı.</td>
                </tr>
              ) : (
                relations.map((relation, index) => (
                  <tr key={relation.relationId}>
                    <td>{index + 1}</td>
                    <td className="mono-cell">{relation.relationName}</td>
                    <td>{relation.descriptionTr || "-"}</td>
                    <td className="mono-cell">{regionName(relation, "source", views)}</td>
                    <td className="mono-cell">{regionName(relation, "target", views)}</td>
                    <td>
                      <div className="action-row">
                        <button className="secondary-button" type="button" onClick={() => openEditModal(relation)}>
                          Düzenle
                        </button>
                        <button className="danger-button" disabled={isSaving} type="button" onClick={() => deleteRelation(relation)}>
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
        title={form.relationId ? "Relation Düzenle" : "Relation Ekle"}
        onClose={() => setIsModalOpen(false)}
      >
        <form className="modal-form modal-form--wide" onSubmit={saveRelation}>
          <div className="form-grid">
            <label>
              Relation Name
              <input
                required
                value={form.relationName}
                onChange={(event) => setForm((current) => ({ ...current, relationName: event.target.value }))}
              />
            </label>
            <label>
              Relation Tanımı
              <input
                value={form.descriptionTr}
                onChange={(event) => setForm((current) => ({ ...current, descriptionTr: event.target.value }))}
              />
            </label>
            <label>
              1. Bölge seçimi
              <select
                value={makeRegionValue(form.sourceRegionType, form.sourceRegionId)}
                onChange={(event) => changeRegion("source", event.target.value)}
              >
                {renderRegionOptions()}
              </select>
            </label>
            <label>
              2. Bölge seçimi
              <select
                value={makeRegionValue(form.targetRegionType, form.targetRegionId)}
                onChange={(event) => changeRegion("target", event.target.value)}
              >
                {renderRegionOptions()}
              </select>
            </label>
          </div>

          <div className="relation-editor">
            <div className="relation-editor__header">
              <h3>Eşleşme alanları</h3>
              <button className="secondary-button" type="button" onClick={addMatchRow}>
                + Satır
              </button>
            </div>
            <div className="relation-match-list">
              {form.columns.map((column, index) => (
                <div className="relation-match-row" key={`${index}-${column.ordinal}`}>
                  <select
                    aria-label="1. bölge kolonu"
                    value={column.sourceColumnName}
                    onChange={(event) => updateMatch(index, "sourceColumnName", event.target.value)}
                  >
                    <option value="">1. bölge kolonu</option>
                    {sourceColumnOptions.map((sourceColumn) => (
                      <option key={sourceColumn.value} value={sourceColumn.value}>
                        {sourceColumn.label}
                      </option>
                    ))}
                  </select>
                  <span className="equals-sign">=</span>
                  <select
                    aria-label="2. bölge kolonu"
                    value={column.targetColumnName}
                    onChange={(event) => updateMatch(index, "targetColumnName", event.target.value)}
                  >
                    <option value="">2. bölge kolonu</option>
                    {targetColumnOptions.map((targetColumn) => (
                      <option key={targetColumn.value} value={targetColumn.value}>
                        {targetColumn.label}
                      </option>
                    ))}
                  </select>
                  <button className="icon-button" type="button" onClick={() => removeMatchRow(index)}>
                    x
                  </button>
                </div>
              ))}
            </div>
          </div>

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

function makeRegionValue(type: RelationRegionType, id: string) {
  return id ? `${type}:${id}` : "";
}

function parseRegionValue(value: string): RegionSelection {
  const separatorIndex = value.indexOf(":");
  if (separatorIndex < 0) {
    return { type: "view", id: value };
  }

  const type = value.slice(0, separatorIndex) === "relation" ? "relation" : "view";
  return { type, id: value.slice(separatorIndex + 1) };
}

function makeColumnRef(viewId: string, columnName: string) {
  return viewId && columnName ? `${viewId}${columnRefSeparator}${columnName}` : "";
}

function parseColumnRef(value: string) {
  const separatorIndex = value.indexOf(columnRefSeparator);
  if (separatorIndex < 0) {
    return { viewId: "", columnName: value.trim() };
  }

  return {
    viewId: value.slice(0, separatorIndex),
    columnName: value.slice(separatorIndex + columnRefSeparator.length).trim()
  };
}

function columnLabel(column: AdminViewColumn) {
  return column.displayNameTr && column.displayNameTr !== column.columnName
    ? `${column.displayNameTr} (${column.columnName})`
    : column.columnName;
}

function regionName(relation: AdminRelation, side: "source" | "target", views: AdminView[]) {
  if (side === "source") {
    return relation.sourceRegionType === "relation"
      ? relation.sourceRegionName || "-"
      : relation.sourceViewName || viewNameById(views, relation.sourceViewId);
  }

  return relation.targetRegionType === "relation"
    ? relation.targetRegionName || "-"
    : relation.targetViewName || viewNameById(views, relation.targetViewId);
}

function viewNameById(views: AdminView[], viewId: string) {
  return views.find((view) => view.viewId === viewId)?.viewName || "-";
}

function getErrorText(error: unknown) {
  return error instanceof Error ? error.message : String(error);
}
