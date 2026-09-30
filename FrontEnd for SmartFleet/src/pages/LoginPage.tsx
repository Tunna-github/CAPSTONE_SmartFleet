import { useState, FormEvent, useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Icon } from "../components/Icons";
import { useAuth } from "../context/AuthContext";

export function LoginPage() {
    const { login, isLoading, isAuthenticated, role } = useAuth();
    const navigate = useNavigate();

    const [usernameOrEmail, setUsernameOrEmail] = useState("");
    const [password, setPassword] = useState("");
    const [error, setError] = useState<string | null>(null);

    // Already logged in → redirect by role
    useEffect(() => {
        if (isAuthenticated && role) {
            const target = role === "admin"
                ? "/admin"
                : role === "maintenance"
                    ? "/dashboard/robots"
                    : "/dashboard";
            navigate(target, { replace: true });
        }
    }, [isAuthenticated, role, navigate]);

    const handleSubmit = async (e: FormEvent<HTMLFormElement>) => {
        e.preventDefault();
        setError(null);

        if (!usernameOrEmail.trim() || !password.trim()) {
            setError("Please enter both username/email and password.");
            return;
        }

        try {
            await login(usernameOrEmail.trim(), password);
        } catch (err: any) {
            setError(err?.message || "Login failed. Check your credentials.");
        }
    };

    return (
        <div
            className="min-h-screen flex items-center justify-center p-4 transition-colors"
            style={{ background: "var(--background)" }}
        >
            <div
                className="w-full max-w-md rounded-2xl p-8 transition-colors"
                style={{
                    background: "var(--surface-2)",
                    border: "1px solid var(--border-subtle)",
                }}
            >
                <div className="flex flex-col items-center mb-8">
                    <div
                        className="w-12 h-12 rounded-xl flex items-center justify-center mb-4"
                        style={{ background: "linear-gradient(135deg,#0ea5e9 0%,#6366f1 100%)" }}
                    >
                        <Icon
                            d={
                                <>
                                    <rect x="3" y="11" width="18" height="10" rx="2" />
                                    <circle cx="12" cy="5" r="2" />
                                    <line x1="12" y1="7" x2="12" y2="11" />
                                </>
                            }
                            size={24}
                        />
                    </div>
                    <h1 className="text-xl font-bold" style={{ color: "var(--text-primary)" }}>
                        SmartFleet WMS
                    </h1>
                    <p className="text-[12px] mt-1" style={{ color: "var(--text-faint)" }}>
                        Sign in to your account
                    </p>
                </div>

                <form onSubmit={handleSubmit} className="space-y-4">
                    <div>
                        <label
                            className="block text-[11px] font-mono mb-1.5"
                            style={{ color: "var(--text-faint)" }}
                        >
                            USERNAME OR EMAIL
                        </label>
                        <input
                            type="text"
                            value={usernameOrEmail}
                            onChange={(e) => setUsernameOrEmail(e.target.value)}
                            placeholder="operator_test"
                            autoComplete="username"
                            className="w-full px-3 py-2 rounded-lg text-[13px] focus:outline-none transition-colors"
                            style={{
                                background: "var(--surface-3)",
                                border: "1px solid var(--border-medium)",
                                color: "var(--text-primary)",
                            }}
                        />
                    </div>

                    <div>
                        <label
                            className="block text-[11px] font-mono mb-1.5"
                            style={{ color: "var(--text-faint)" }}
                        >
                            PASSWORD
                        </label>
                        <input
                            type="password"
                            value={password}
                            onChange={(e) => setPassword(e.target.value)}
                            placeholder="••••••••"
                            autoComplete="current-password"
                            className="w-full px-3 py-2 rounded-lg text-[13px] focus:outline-none transition-colors"
                            style={{
                                background: "var(--surface-3)",
                                border: "1px solid var(--border-medium)",
                                color: "var(--text-primary)",
                            }}
                        />
                    </div>

                    {error && (
                        <div
                            className="text-[12px] px-3 py-2 rounded-lg"
                            style={{
                                background: "rgba(239,68,68,0.08)",
                                color: "#f87171",
                                border: "1px solid rgba(239,68,68,0.3)",
                            }}
                        >
                            {error}
                        </div>
                    )}

                    <button
                        type="submit"
                        disabled={isLoading}
                        className="w-full py-2.5 rounded-lg text-[13px] font-bold transition-colors disabled:opacity-60"
                        style={{ background: "#22d3ee", color: "#07091a" }}
                    >
                        {isLoading ? "Signing in…" : "Sign In"}
                    </button>
                </form>

                <div
                    className="mt-6 p-3 rounded-lg text-[11px] text-center"
                    style={{
                        background: "rgba(99,102,241,0.08)",
                        border: "1px solid rgba(99,102,241,0.2)",
                        color: "#818cf8",
                    }}
                >
                    Test users: <strong>operator_test</strong>, <strong>admin_test</strong>, <strong>maintenance_test</strong>
                </div>
            </div>
        </div>
    );
}