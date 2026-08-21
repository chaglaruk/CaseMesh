"use client";
import { FormEvent, useState } from "react";
import { useRouter } from "next/navigation";

export default function SignInPage() {
  const router = useRouter(); const [error,setError]=useState("");
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); setError(""); const form=new FormData(event.currentTarget);
    const response=await fetch("/api/auth/test-sign-in",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({subject:form.get("subject"),displayName:form.get("name")})});
    if(!response.ok){setError("Sign-in is unavailable. Use your configured identity provider.");return;} router.push("/matters");
  }
  return <main><section className="card" style={{maxWidth:520,margin:"auto"}}><h1>Sign in to CaseMesh</h1><p className="lede">Your workspace is private. Production sign-in uses your configured identity provider and keeps tokens off the browser.</p><form onSubmit={submit}><label>Display name<input name="name" defaultValue="Synthetic Pilot User" required maxLength={100}/></label><label>Test subject<input name="subject" defaultValue="synthetic-pilot-user" required maxLength={100}/></label>{error&&<p role="alert" className="error">{error}</p>}<button type="submit">Continue securely</button></form></section></main>;
}
