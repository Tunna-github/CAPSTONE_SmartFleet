import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Icon } from "../components/Icons";

export function LoginPage({ onLogin }: { onLogin: (role: "operator" | "admin") => void }) {
    const [email, setEmail] = useState("");
    const [password, setPassword] = useState("");
    const navigate = useNavigate();

    const handleLogin = (e: React.FormEvent) => {
        e.preventDefault();
        const role = email.includes("admin") ? "admin" : "operator";
        onLogin(role);
        navigate(role === "admin" ? "/admin" : "/dashboard");
    };

    return (
        <div className="min-h-screen flex items-center justify-center p-4" style={{ background: "#070d1e" }}>
            <div className="w-full max-w-md rounded-2xl p-8" style={{ background: "#0c1128", border: "1px solid #151d35" }}>
                <div className="flex flex-col items-center mb-8">
                    <div className="w-12 h-12 rounded-xl flex items-center justify-center mb-4" style={{ background: "linear-gradient(135deg,#0ea5e9 0%,#6366f1 100%)" }}>
                        <Icon d={<><rect x="3" y="11" width="18" height="10" rx="2" /><circle cx="12" cy="5" r="2" /><line x1="12" y1="7" x2="12" y2="11" /></>} size={24} />
                    </div>
                    <h1 className="text-xl font-bold text-white">SmartFleet WMS</h1>
                    <p className="text-[12px] mt-1" style={{ color: "#4a5a80" }}>Sign in to your account</p>
                </div>

                <form onSubmit={handleLogin} className="space-y-4">
                    <div>
                        <label className="block text-[11px] font-mono mb-1.5" style={{ color: "#4a5a80" }}>EMAIL</label>
                        <input type="email" value={email} onChange={e => setEmail(e.target.value)}
                            className="w-full px-3 py-2 rounded-lg text-[13px] text-white focus:outline-none"
                            style={{ background: "#07091a", border: "1px solid #151d35" }} placeholder="operator@smartfleet.com" required />
                    </div>
                    <div>
                        <label className="block text-[11px] font-mono mb-1.5" style={{ color: "#4a5a80" }}>PASSWORD</label>
                        <input type="password" value={password} onChange={e => setPassword(e.target.value)}
                            className="w-full px-3 py-2 rounded-lg text-[13px] text-white focus:outline-none"
                            style={{ background: "#07091a", border: "1px solid #151d35" }} placeholder="••••••••" required />
                    </div>
                    <button type="submit" className="w-full py-2.5 rounded-lg text-[13px] font-bold transition-colors" style={{ background: "#22d3ee", color: "#07091a" }}>
                        Sign In
                    </button>
                </form>

                <div className="mt-6 p-3 rounded-lg text-[11px] text-center" style={{ background: "rgba(99,102,241,0.08)", border: "1px solid rgba(99,102,241,0.2)", color: "#818cf8" }}>
                    Mock Login: Use any email. Add "admin" in email to login as Admin.
                </div>
            </div>
        </div>
    );
}