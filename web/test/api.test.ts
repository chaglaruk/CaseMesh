import { afterEach, describe, expect, it, vi } from "vitest";
import { request, tenantValue } from "../lib/api";

afterEach(()=>vi.unstubAllGlobals());
describe("BFF client",()=>{
  it("reads a serialized tenant identity",()=>expect(tenantValue({tenantId:"tenant-a",workspaceName:"A",role:1})).toBe("tenant-a"));
  it("reads a value-object tenant identity",()=>expect(tenantValue({tenantId:{value:"tenant-b"},workspaceName:"B",role:2})).toBe("tenant-b"));
  it("rejects an empty tenant identity",()=>expect(()=>tenantValue({tenantId:{value:""},workspaceName:"Invalid",role:1})).toThrow("Workspace identity is invalid"));
  it("does not fetch a CSRF token for safe reads",async()=>{const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify({ok:true}),{status:200,headers:{"Content-Type":"application/json"}}));vi.stubGlobal("fetch",fetch);await request("/safe");expect(fetch).toHaveBeenCalledTimes(1);});
  it("adds a server-issued CSRF token to writes",async()=>{const fetch=vi.fn().mockResolvedValueOnce(new Response(JSON.stringify({token:"csrf"}),{status:200})).mockResolvedValueOnce(new Response(null,{status:204}));vi.stubGlobal("fetch",fetch);await request("/write",{method:"POST"});expect((fetch.mock.calls[1][1].headers as Headers).get("X-CSRF-TOKEN")).toBe("csrf");});
  it("surfaces typed ProblemDetails without evidence logging",async()=>{vi.stubGlobal("fetch",vi.fn().mockResolvedValue(new Response(JSON.stringify({title:"Invalid request",detail:"Safe detail"}),{status:400})));await expect(request("/bad")).rejects.toThrow("Safe detail");});
  it("redirects authentication failures",async()=>{const navigate=vi.fn();vi.stubGlobal("fetch",vi.fn().mockResolvedValue(new Response(null,{status:401})));await expect(request("/private",undefined,navigate)).rejects.toThrow("Authentication required");expect(navigate).toHaveBeenCalledWith("/sign-in");});
});
