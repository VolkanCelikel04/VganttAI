import type {
  AdminDashboard,
  AdminRelation,
  AdminUser,
  AdminView,
  AdminViewColumn,
  LoginResult,
  SaveAdminRelationInput,
  SaveAdminViewColumnInput,
  SaveAdminViewInput
} from "@/types/admin";

const apiBaseUrl = (
  process.env.NEXT_PUBLIC_API_BASE_URL || "http://127.0.0.1:5055"
).replace(/\/$/, "");

type RequestOptions = Omit<RequestInit, "body"> & {
  body?: unknown;
  token?: string | null;
};

class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
    this.name = "ApiError";
  }
}

async function readError(response: Response) {
  try {
    const payload = (await response.json()) as { message?: string };
    return payload.message || response.statusText;
  } catch {
    return response.statusText;
  }
}

async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const headers = new Headers(options.headers);

  if (options.token) {
    headers.set("authorization", `Bearer ${options.token}`);
  }

  let body: BodyInit | undefined;
  if (options.body !== undefined) {
    headers.set("content-type", "application/json");
    body = JSON.stringify(options.body);
  }

  const response = await fetch(`${apiBaseUrl}${path}`, {
    ...options,
    headers,
    body
  });

  if (!response.ok) {
    throw new ApiError(await readError(response), response.status);
  }

  if (response.status === 204) {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const adminApi = {
  login(username: string, password: string) {
    return request<LoginResult>("/admin/api/login", {
      method: "POST",
      body: { username, password }
    });
  },

  me(token: string) {
    return request<AdminUser>("/admin/api/me", { token });
  },

  dashboard(token: string) {
    return request<AdminDashboard>("/admin/api/dashboard", { token });
  },

  listViews(token: string) {
    return request<AdminView[]>("/admin/api/views", { token });
  },

  createView(token: string, input: SaveAdminViewInput) {
    return request<AdminView>("/admin/api/views", {
      method: "POST",
      token,
      body: input
    });
  },

  updateView(token: string, input: SaveAdminViewInput) {
    return request<AdminView>("/admin/api/views", {
      method: "PUT",
      token,
      body: input
    });
  },

  deleteView(token: string, viewId: string) {
    return request<{ deleted: boolean }>("/admin/api/views?viewId=" + encodeURIComponent(viewId), {
      method: "DELETE",
      token
    });
  },

  listColumns(token: string, viewId: string) {
    return request<AdminViewColumn[]>(
      "/admin/api/view-columns?viewId=" + encodeURIComponent(viewId),
      { token }
    );
  },

  saveColumns(token: string, viewId: string, columns: SaveAdminViewColumnInput[]) {
    return request<{ viewId: string; savedCount: number }>("/admin/api/view-columns", {
      method: "POST",
      token,
      body: { viewId, columns }
    });
  },

  listRelations(token: string) {
    return request<AdminRelation[]>("/admin/api/relations", { token });
  },

  createRelation(token: string, input: SaveAdminRelationInput) {
    return request<AdminRelation>("/admin/api/relations", {
      method: "POST",
      token,
      body: input
    });
  },

  updateRelation(token: string, input: SaveAdminRelationInput) {
    return request<AdminRelation>("/admin/api/relations", {
      method: "PUT",
      token,
      body: input
    });
  },

  deleteRelation(token: string, relationId: string) {
    return request<{ deleted: boolean }>(
      "/admin/api/relations?relationId=" + encodeURIComponent(relationId),
      {
        method: "DELETE",
        token
      }
    );
  }
};

export { ApiError };
