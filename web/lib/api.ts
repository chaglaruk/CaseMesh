export type Membership = { tenantId: { value: string } | string; workspaceName: string; role: number };
export type Matter = { id: string; title: string; status: string; matterType: string; jurisdiction?: string; updatedAt: string };

export async function request<T>(path: string, init?: RequestInit,
  navigate: (destination: string) => void = destination => window.location.assign(destination)): Promise<T> {
  const method = init?.method?.toUpperCase() ?? "GET";
  const headers = new Headers(init?.headers);
  if (!["GET", "HEAD", "OPTIONS"].includes(method)) {
    const csrf = await fetch("/api/auth/csrf", { credentials: "include" });
    if (!csrf.ok) throw new Error("Your session is unavailable.");
    headers.set("X-CSRF-TOKEN", (await csrf.json()).token);
  }
  const response = await fetch(`/api${path}`, { ...init, headers, credentials: "include" });
  if (response.status === 401) { navigate("/sign-in"); throw new Error("Authentication required."); }
  if (!response.ok) {
    const problem = await response.json().catch(() => ({ title: "Request failed" }));
    throw new Error(problem.detail ?? problem.title ?? "Request failed");
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export function tenantValue(membership: Membership): string {
  const tenantId = typeof membership.tenantId === "string" ? membership.tenantId : membership.tenantId.value;
  if (!tenantId?.trim()) throw new Error("Workspace identity is invalid.");
  return tenantId;
}
