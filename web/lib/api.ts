export type Membership = { tenantId: { value?: string } | string; workspaceName: string; role: number };
export type Matter = { id: string; title: string; status: string; matterType: string; jurisdiction?: string; updatedAt: string };

export async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const method = init?.method?.toUpperCase() ?? "GET";
  const headers = new Headers(init?.headers);
  if (!["GET", "HEAD", "OPTIONS"].includes(method)) {
    const csrf = await fetch("/api/auth/csrf", { credentials: "include" });
    if (!csrf.ok) throw new Error("Your session is unavailable.");
    headers.set("X-CSRF-TOKEN", (await csrf.json()).token);
  }
  const response = await fetch(`/api${path}`, { ...init, headers, credentials: "include" });
  if (response.status === 401) { window.location.assign("/sign-in"); throw new Error("Authentication required."); }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: "Request failed" }));
    throw new Error(problem.detail ?? problem.title ?? "Request failed");
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export function tenantValue(membership: Membership): string {
  return typeof membership.tenantId === "string" ? membership.tenantId : membership.tenantId.value ?? String(membership.tenantId);
}
