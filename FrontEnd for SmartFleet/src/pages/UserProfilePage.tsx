import { useEffect, useState } from "react";
import { getMyProfile } from "../api/modules/users";
import { Icon, IC } from "../components/Icons";
import { PageHeader } from "../components/SharedUI";
import { useAuth } from "../context/AuthContext";

function getInitials(name: string) {
    return name
        .trim()
        .split(/\s+/)
        .slice(0, 2)
        .map((part) => part[0]?.toUpperCase() ?? "")
        .join("");
}

export function UserProfilePage() {
    const { user, role, updateUser } = useAuth();
    const [profile, setProfile] = useState(user);
    const [isLoading, setIsLoading] = useState(true);
    const [loadError, setLoadError] = useState("");

    useEffect(() => {
        let isMounted = true;

        getMyProfile()
            .then((currentUser) => {
                if (!isMounted) return;
                setProfile(currentUser);
                updateUser(currentUser);
            })
            .catch((error: unknown) => {
                if (!isMounted) return;
                const message = typeof error === "object" && error !== null && "message" in error
                    ? String(error.message)
                    : "Unable to load your profile. Showing saved account information.";
                setLoadError(message);
            })
            .finally(() => {
                if (isMounted) setIsLoading(false);
            });

        return () => {
            isMounted = false;
        };
    }, [updateUser]);

    const name = profile?.fullName || profile?.username || "SmartFleet User";
    const roleLabel = role === "admin"
        ? "Administrator"
        : role === "maintenance"
            ? "Maintenance Technician"
            : "Fleet Operator";
    const details = [
        { label: "Full name", value: profile?.fullName || "Not provided" },
        { label: "Username", value: profile?.username || "Not provided" },
        { label: "Email address", value: profile?.email || "Not provided" },
        { label: "Phone number", value: profile?.phoneNumber || "Not provided" },
        { label: "User ID", value: profile?.userId ? `SF-${profile.userId}` : "Not available" },
    ];

    return (
        <div className="flex flex-col h-full overflow-hidden" style={{ background: "var(--background)" }}>
            <PageHeader title="User Profile" sub="Account details · Identity · Access" />

            <main className="flex-1 overflow-y-auto p-4 sm:p-6">
                <div className="mx-auto max-w-5xl space-y-5">
                    {(isLoading || loadError) && (
                        <div
                            className="rounded-lg px-4 py-3 text-[12px]"
                            role={loadError ? "alert" : "status"}
                            style={{
                                background: loadError ? "rgba(245,158,11,0.1)" : "var(--surface-2)",
                                border: `1px solid ${loadError ? "rgba(245,158,11,0.25)" : "var(--border-subtle)"}`,
                                color: loadError ? "#fbbf24" : "var(--text-faint)",
                            }}
                        >
                            {loadError || "Loading profile..."}
                        </div>
                    )}
                    <section
                        className="relative overflow-hidden rounded-2xl p-5 sm:p-7"
                        style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                    >
                        <div
                            className="absolute -right-12 -top-16 h-48 w-48 rounded-full blur-3xl pointer-events-none"
                            style={{ background: "rgba(34,211,238,0.12)" }}
                        />
                        <div className="relative flex flex-col gap-5 sm:flex-row sm:items-center">
                            <div
                                className="flex h-20 w-20 shrink-0 items-center justify-center rounded-2xl text-2xl font-bold text-white"
                                style={{ background: "linear-gradient(135deg,#0891b2,#4f46e5)" }}
                            >
                                {getInitials(name) || "SF"}
                            </div>
                            <div className="min-w-0 flex-1">
                                <p className="mb-1 font-mono text-[10px] font-semibold tracking-widest" style={{ color: "#22d3ee" }}>
                                    ACCOUNT PROFILE
                                </p>
                                <h2 className="wrap-break-word text-2xl font-bold" style={{ color: "var(--text-primary)" }}>
                                    {name}
                                </h2>
                                <p className="mt-1 text-sm" style={{ color: "var(--text-faint)" }}>
                                    {profile?.email || "No email address on file"}
                                </p>
                                <div className="mt-3 flex flex-wrap items-center gap-2">
                                    <span
                                        className="rounded-md px-2.5 py-1 font-mono text-[10px] font-semibold"
                                        style={{ background: "rgba(34,211,238,0.1)", color: "#22d3ee", border: "1px solid rgba(34,211,238,0.22)" }}
                                    >
                                        {roleLabel}
                                    </span>
                                    {profile?.isActive !== undefined && (
                                        <span
                                            className="flex items-center gap-1.5 rounded-md px-2.5 py-1 font-mono text-[10px] font-semibold"
                                            style={{
                                                background: profile.isActive ? "rgba(34,197,94,0.1)" : "rgba(239,68,68,0.1)",
                                                color: profile.isActive ? "#4ade80" : "#f87171",
                                                border: `1px solid ${profile.isActive ? "rgba(34,197,94,0.22)" : "rgba(239,68,68,0.22)"}`,
                                            }}
                                        >
                                            <span className="h-1.5 w-1.5 rounded-full" style={{ background: "currentColor" }} />
                                            {profile.isActive ? "ACTIVE" : "INACTIVE"}
                                        </span>
                                    )}
                                </div>
                            </div>
                            <div className="flex items-center gap-2 self-start rounded-lg px-3 py-2 text-[11px]" style={{ background: "var(--surface-3)", color: "var(--text-faint)" }}>
                                <Icon d={IC.check} size={14} />
                                Signed in
                            </div>
                        </div>
                    </section>

                    <div className="grid gap-5 lg:grid-cols-[1.5fr_1fr]">
                        <section
                            className="rounded-2xl p-5 sm:p-6"
                            style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                        >
                            <div className="mb-4 flex items-center gap-3">
                                <span className="flex h-9 w-9 items-center justify-center rounded-lg" style={{ background: "rgba(34,211,238,0.1)", color: "#22d3ee" }}>
                                    <Icon d={IC.info} size={17} />
                                </span>
                                <div>
                                    <h3 className="text-sm font-bold" style={{ color: "var(--text-primary)" }}>Personal information</h3>
                                    <p className="mt-0.5 text-[11px]" style={{ color: "var(--text-faint)" }}>Details associated with your account</p>
                                </div>
                            </div>
                            <div>
                                {details.map(({ label, value }) => (
                                    <div key={label} className="grid gap-1 border-t py-3 sm:grid-cols-[150px_1fr] sm:gap-4" style={{ borderColor: "var(--border-subtle)" }}>
                                        <span className="text-[11px]" style={{ color: "var(--text-faint)" }}>{label}</span>
                                        <span className="break-all text-[13px] font-medium" style={{ color: "var(--text-primary)" }}>{value}</span>
                                    </div>
                                ))}
                            </div>
                        </section>

                        <section
                            className="rounded-2xl p-5 sm:p-6"
                            style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}
                        >
                            <div className="mb-4 flex items-center gap-3">
                                <span className="flex h-9 w-9 items-center justify-center rounded-lg" style={{ background: "rgba(99,102,241,0.12)", color: "#818cf8" }}>
                                    <Icon d={IC.settings} size={17} />
                                </span>
                                <div>
                                    <h3 className="text-sm font-bold" style={{ color: "var(--text-primary)" }}>Access & roles</h3>
                                    <p className="mt-0.5 text-[11px]" style={{ color: "var(--text-faint)" }}>Permissions assigned to your account</p>
                                </div>
                            </div>
                            <div className="space-y-2">
                                {(profile?.roles?.length ? profile.roles : [roleLabel]).map((accountRole) => (
                                    <div key={accountRole} className="flex items-center justify-between gap-3 rounded-lg px-3 py-2.5" style={{ background: "var(--surface-3)" }}>
                                        <span className="text-[12px] font-medium" style={{ color: "var(--text-primary)" }}>{accountRole}</span>
                                        <Icon d={IC.check} size={14} />
                                    </div>
                                ))}
                            </div>
                            <p className="mt-4 border-t pt-4 text-[11px] leading-relaxed" style={{ borderColor: "var(--border-subtle)", color: "var(--text-faint)" }}>
                                Contact your system administrator to change account details or permissions.
                            </p>
                        </section>
                    </div>
                </div>
            </main>
        </div>
    );
}