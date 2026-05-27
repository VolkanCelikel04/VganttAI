export type AdminUser = {
  userDisplayName: string;
  tenantName: string;
  tenantCode: string;
  role: string;
};

export type LoginResult = {
  token: string;
  tenantName: string;
  userDisplayName: string;
  role: string;
};

export type AdminView = {
  viewId: string;
  viewName: string;
  displayNameTr: string;
  descriptionTr: string;
  isActive: boolean;
};

export type SaveAdminViewInput = {
  viewId?: string | null;
  viewName: string;
  displayNameTr: string;
  descriptionTr: string;
  isActive: boolean;
};

export type AdminViewColumn = {
  columnId: string;
  viewId: string;
  columnName: string;
  displayNameTr: string;
  dataType: string;
  semanticMeaningTr: string;
  isFilterable: boolean;
  isGroupable: boolean;
  isSummable: boolean;
  isActive: boolean;
};

export type SaveAdminViewColumnInput = {
  columnName: string;
  displayNameTr: string;
  dataType: string;
  semanticMeaningTr: string;
  isFilterable: boolean;
  isGroupable: boolean;
  isSummable: boolean;
  isActive: boolean;
};

export type AdminRelationColumn = {
  relationColumnId?: string;
  sourceColumnName: string;
  targetColumnName: string;
  ordinal: number;
};

export type AdminRelation = {
  relationId: string;
  relationName: string;
  sourceViewId: string;
  sourceViewName: string;
  targetViewId: string;
  targetViewName: string;
  sourceRegionType?: "view" | "relation";
  sourceRelationId?: string | null;
  sourceRegionName?: string;
  targetRegionType?: "view" | "relation";
  targetRelationId?: string | null;
  targetRegionName?: string;
  joinType: string;
  descriptionTr: string;
  isActive: boolean;
  columns: AdminRelationColumn[];
};

export type SaveAdminRelationInput = {
  relationId?: string | null;
  relationName: string;
  sourceViewId: string;
  targetViewId: string;
  sourceRegionType?: "view" | "relation";
  sourceRelationId?: string | null;
  targetRegionType?: "view" | "relation";
  targetRelationId?: string | null;
  joinType: string;
  descriptionTr: string;
  isActive: boolean;
  columns: AdminRelationColumn[];
};

export type AdminDashboard = {
  viewCount: number;
  columnCount: number;
  relationCount: number;
};
