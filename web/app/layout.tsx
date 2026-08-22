import type { Metadata } from "next";
import "./styles.css";

export const metadata: Metadata = { title: "CaseMesh", description: "Evidence workspace for workplace disputes" };
// Per-request rendering is required so Next.js can apply the proxy-generated CSP nonce to runtime scripts.
export const dynamic = "force-dynamic";
export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return <html lang="en"><body><a className="skip-link" href="#main-content">Skip to main content</a><header className="topbar"><a href="/matters" className="brand">CaseMesh</a><span>Evidence workspace</span></header><div id="main-content" tabIndex={-1}>{children}</div></body></html>;
}
