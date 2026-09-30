import { useCallback, useEffect, useState, type FormEvent } from "react";
import { activateUser, createUser, deactivateUser, getUsers, updateUser } from "../api/modules/users";
import type { AuthUser } from "../api/types";
import { Icon, IC } from "../components/Icons";
import { PageHeader } from "../components/SharedUI";

type StatusFilter = "all" | "active" | "inactive";
type UserFormData = {
    fullName: string;
    username: string;
    email: string;
    phoneNumber: string;
    password: string;
    roles: string[];
};

const ROLE_OPTIONS = ["Warehouse Operator", "Maintenance Technician", "Administrator"];
const EMPTY_USER_FORM: UserFormData = {
    fullName: "",
    username: "",
    email: "",
    phoneNumber: "",
    password: "",
    roles: [ROLE_OPTIONS[0]],
};

function getInitials(name: string) {
    return name
        .trim()
        .split(/\s+/)
        .slice(0, 2)
        .map((part) => part[0]?.toUpperCase() ?? "")
        .join("") || "U";
}

function errorMessage(error: unknown) {
    if (typeof error === "object" && error !== null && "message" in error) {
        return String(error.message);
    }
    return "Unable to load users. Please try again.";
}

export function AdminUsersPage() {
    const [users, setUsers] = useState<AuthUser[]>([]);
    const [search, setSearch] = useState("");
    const [statusFilter, setStatusFilter] = useState<StatusFilter>("all");
    const [isLoading, setIsLoading] = useState(true);
    const [error, setError] = useState("");
    const [actionError, setActionError] = useState("");
    const [updatingUserId, setUpdatingUserId] = useState<number | null>(null);
    const [editingUser, setEditingUser] = useState<AuthUser | null>(null);
    const [userForm, setUserForm] = useState<UserFormData>(EMPTY_USER_FORM);
    const [isFormOpen, setIsFormOpen] = useState(false);
    const [isSavingUser, setIsSavingUser] = useState(false);

    const loadUsers = useCallback(async () => {
        setIsLoading(true);
        setError("");
        try {
            setUsers(await getUsers());
        } catch (requestError) {
            setError(errorMessage(requestError));
        } finally {
            setIsLoading(false);
        }
    }, []);

    useEffect(() => {
        void loadUsers();
    }, [loadUsers]);

    const toggleUserStatus = async (user: AuthUser) => {
        setUpdatingUserId(user.userId);
        setActionError("");
        try {
            const nextIsActive = user.isActive !== true;
            if (nextIsActive) {
                await activateUser(user.userId);
            } else {
                await deactivateUser(user.userId);
            }
            setUsers((currentUsers) => currentUsers.map((currentUser) =>
                currentUser.userId === user.userId
                    ? { ...currentUser, isActive: nextIsActive }
                    : currentUser
            ));
            await loadUsers();
        } catch (requestError) {
            setActionError(errorMessage(requestError));
        } finally {
            setUpdatingUserId(null);
        }
    };

    const openCreateForm = () => {
        setEditingUser(null);
        setUserForm({ ...EMPTY_USER_FORM, roles: [...EMPTY_USER_FORM.roles] });
        setActionError("");
        setIsFormOpen(true);
    };

    const openEditForm = (user: AuthUser) => {
        setEditingUser(user);
        setUserForm({
            fullName: user.fullName ?? "",
            username: user.username ?? "",
            email: user.email ?? "",
            phoneNumber: user.phoneNumber ?? "",
            password: "",
            roles: user.roles ?? [],
        });
        setActionError("");
        setIsFormOpen(true);
    };

    const handleSaveUser = async (event: FormEvent<HTMLFormElement>) => {
        event.preventDefault();
        if (userForm.roles.length === 0) {
            setActionError("Select at least one role for this user.");
            return;
        }
        setIsSavingUser(true);
        setActionError("");
        try {
            const userFields = {
                fullName: userForm.fullName.trim(),
                username: userForm.username.trim(),
                email: userForm.email.trim(),
                phoneNumber: userForm.phoneNumber.trim() || undefined,
                roles: userForm.roles,
            };
            if (editingUser) {
                await updateUser(editingUser.userId, {
                    ...userFields,
                    isActive: editingUser.isActive ?? false,
                });
            } else {
                await createUser({ ...userFields, password: userForm.password });
            }
            setIsFormOpen(false);
            await loadUsers();
        } catch (requestError) {
            setActionError(errorMessage(requestError));
        } finally {
            setIsSavingUser(false);
        }
    };

    const toggleFormRole = (role: string) => {
        setUserForm((current) => ({
            ...current,
            roles: current.roles.includes(role)
                ? current.roles.filter((currentRole) => currentRole !== role)
                : [...current.roles, role],
        }));
    };

    const activeCount = users.filter((user) => user.isActive === true).length;
    const inactiveCount = users.filter((user) => user.isActive === false).length;
    const query = search.trim().toLowerCase();
    const filteredUsers = users.filter((user) => {
        const searchable = [user.fullName, user.username, user.email, ...(user.roles ?? [])]
            .filter(Boolean)
            .join(" ")
            .toLowerCase();
        const matchesSearch = !query || searchable.includes(query);
        const matchesStatus = statusFilter === "all"
            || (statusFilter === "active" && user.isActive === true)
            || (statusFilter === "inactive" && user.isActive === false);
        return matchesSearch && matchesStatus;
    });
    const displayedRoleOptions = Array.from(new Set([...ROLE_OPTIONS, ...userForm.roles]));

    return (
        <div className="flex h-full flex-col overflow-hidden" style={{ background: "var(--background)" }}>
            <PageHeader title="User Management" sub="Directory · Roles · Account status">
                <button
                    type="button"
                    onClick={openCreateForm}
                    className="inline-flex h-9 items-center gap-2 rounded-lg px-3 text-[12px] font-semibold"
                    style={{ background: "#22d3ee", color: "#07111b" }}
                >
                    <Icon d={IC.plus} size={14} />
                    Add user
                </button>
                <button
                    type="button"
                    onClick={() => void loadUsers()}
                    disabled={isLoading}
                    title="Refresh users"
                    aria-label="Refresh users"
                    className="flex h-9 w-9 items-center justify-center rounded-lg transition-colors disabled:opacity-50"
                    style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-secondary)" }}
                >
                    <Icon d={IC.refresh} size={15} />
                </button>
            </PageHeader>

            <main className="flex-1 overflow-y-auto p-4 sm:p-6">
                <div className="mx-auto max-w-6xl space-y-5">
                    <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
                        {[
                            { label: "Total users", value: users.length, color: "#22d3ee" },
                            { label: "Active", value: activeCount, color: "#4ade80" },
                            { label: "Inactive", value: inactiveCount, color: "#f87171" },
                        ].map((stat) => (
                            <div key={stat.label} className="flex items-center justify-between rounded-xl px-4 py-3" style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}>
                                <span className="text-[12px]" style={{ color: "var(--text-faint)" }}>{stat.label}</span>
                                <span className="font-mono text-xl font-bold" style={{ color: stat.color }}>{stat.value}</span>
                            </div>
                        ))}
                    </div>

                    <section className="overflow-hidden rounded-xl" style={{ background: "var(--surface-2)", border: "1px solid var(--border-subtle)" }}>
                        <div className="flex flex-col gap-3 border-b p-4 sm:flex-row sm:items-center sm:justify-between" style={{ borderColor: "var(--border-subtle)" }}>
                            <div>
                                <h2 className="text-sm font-bold" style={{ color: "var(--text-primary)" }}>All users</h2>
                                <p className="mt-0.5 text-[11px]" style={{ color: "var(--text-faint)" }}>
                                    {filteredUsers.length} of {users.length} accounts
                                </p>
                            </div>
                            <div className="flex flex-col gap-2 sm:flex-row">
                                <label className="relative">
                                    <span className="sr-only">Search users</span>
                                    <span className="absolute left-3 top-1/2 -translate-y-1/2" style={{ color: "var(--text-faint)" }}>
                                        <Icon d={IC.search} size={14} />
                                    </span>
                                    <input
                                        type="search"
                                        value={search}
                                        onChange={(event) => setSearch(event.target.value)}
                                        placeholder="Search name, email, role..."
                                        className="w-full rounded-lg py-2 pl-9 pr-3 text-[12px] outline-none sm:w-64"
                                        style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
                                    />
                                </label>
                                <select
                                    aria-label="Filter users by status"
                                    value={statusFilter}
                                    onChange={(event) => setStatusFilter(event.target.value as StatusFilter)}
                                    className="rounded-lg px-3 py-2 text-[12px] outline-none"
                                    style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }}
                                >
                                    <option value="all">All statuses</option>
                                    <option value="active">Active</option>
                                    <option value="inactive">Inactive</option>
                                </select>
                            </div>
                        </div>

                        {error && (
                            <div className="m-4 flex flex-wrap items-center justify-between gap-3 rounded-lg px-4 py-3 text-[12px]" role="alert" style={{ background: "rgba(239,68,68,0.1)", border: "1px solid rgba(239,68,68,0.25)", color: "#f87171" }}>
                                <span>{error}</span>
                                <button type="button" onClick={() => void loadUsers()} className="font-semibold underline underline-offset-2">Retry</button>
                            </div>
                        )}
                        {actionError && !isFormOpen && (
                            <div className="m-4 rounded-lg px-4 py-3 text-[12px]" role="alert" style={{ background: "rgba(239,68,68,0.1)", border: "1px solid rgba(239,68,68,0.25)", color: "#f87171" }}>
                                {actionError}
                            </div>
                        )}

                        <div className="overflow-x-auto">
                            <table className="w-full border-collapse text-left" style={{ minWidth: 880 }}>
                                <thead>
                                    <tr style={{ background: "var(--surface-1)", borderBottom: "1px solid var(--border-subtle)" }}>
                                        {["User", "Email", "Phone", "Roles", "Status", "Actions"].map((heading) => (
                                            <th key={heading} className="px-4 py-3 font-mono text-[10px] font-semibold tracking-wide" style={{ color: "var(--text-faint)" }}>{heading}</th>
                                        ))}
                                    </tr>
                                </thead>
                                <tbody>
                                    {isLoading && users.length === 0 ? (
                                        <tr><td colSpan={6} className="px-4 py-12 text-center text-[12px]" style={{ color: "var(--text-faint)" }}>Loading users...</td></tr>
                                    ) : filteredUsers.length === 0 ? (
                                        <tr><td colSpan={6} className="px-4 py-12 text-center text-[12px]" style={{ color: "var(--text-faint)" }}>{error ? "User list is unavailable." : "No users match your search."}</td></tr>
                                    ) : filteredUsers.map((user) => (
                                        <tr key={user.userId} className="border-b last:border-b-0" style={{ borderColor: "var(--border-subtle)" }}>
                                            <td className="px-4 py-3">
                                                <div className="flex items-center gap-3">
                                                    <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full text-[11px] font-bold text-white" style={{ background: "linear-gradient(135deg,#0891b2,#4f46e5)" }}>
                                                        {getInitials(user.fullName || user.username || "User")}
                                                    </span>
                                                    <div className="min-w-0">
                                                        <div className="max-w-56 truncate text-[12px] font-semibold" style={{ color: "var(--text-primary)" }}>{user.fullName || "Name not provided"}</div>
                                                        <div className="text-[10px]" style={{ color: "var(--text-faint)" }}>@{user.username}</div>
                                                    </div>
                                                </div>
                                            </td>
                                            <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{user.email || "—"}</td>
                                            <td className="px-4 py-3 text-[12px]" style={{ color: "var(--text-secondary)" }}>{user.phoneNumber || "—"}</td>
                                            <td className="px-4 py-3">
                                                <div className="flex flex-wrap gap-1.5">
                                                    {user.roles?.length ? user.roles.map((role) => (
                                                        <span key={role} className="rounded px-2 py-1 font-mono text-[9px]" style={{ background: "rgba(34,211,238,0.08)", border: "1px solid rgba(34,211,238,0.18)", color: "#22d3ee" }}>{role}</span>
                                                    )) : <span className="text-[11px]" style={{ color: "var(--text-faint)" }}>No role</span>}
                                                </div>
                                            </td>
                                            <td className="px-4 py-3">
                                                <span className="inline-flex items-center gap-1.5 rounded px-2 py-1 font-mono text-[9px] font-semibold" style={{
                                                    background: user.isActive === true ? "rgba(34,197,94,0.1)" : user.isActive === false ? "rgba(239,68,68,0.1)" : "var(--surface-3)",
                                                    color: user.isActive === true ? "#4ade80" : user.isActive === false ? "#f87171" : "var(--text-faint)",
                                                }}>
                                                    <span className="h-1.5 w-1.5 rounded-full" style={{ background: "currentColor" }} />
                                                    {user.isActive === true ? "ACTIVE" : user.isActive === false ? "INACTIVE" : "UNKNOWN"}
                                                </span>
                                            </td>
                                            <td className="px-4 py-3">
                                                <div className="flex items-center gap-2">
                                                <button
                                                    type="button"
                                                    onClick={() => openEditForm(user)}
                                                    disabled={updatingUserId !== null || isSavingUser}
                                                    aria-label={`Edit ${user.fullName || user.username}`}
                                                    title="Edit user"
                                                    className="inline-flex h-8 w-8 items-center justify-center rounded-md transition-colors disabled:opacity-50"
                                                    style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-secondary)" }}
                                                >
                                                    <Icon d={IC.pencil} size={14} />
                                                </button>
                                                <button
                                                    type="button"
                                                    onClick={() => void toggleUserStatus(user)}
                                                    disabled={updatingUserId !== null || isSavingUser}
                                                    role="switch"
                                                    aria-checked={user.isActive === true}
                                                    aria-label={`${user.isActive === true ? "Deactivate" : "Activate"} ${user.fullName || user.username}`}
                                                    title={user.isActive === true ? "Deactivate account" : "Activate account"}
                                                    className="inline-flex items-center gap-2 rounded-md p-1 text-[11px] font-semibold disabled:cursor-wait disabled:opacity-50"
                                                >
                                                    <span
                                                        className="relative inline-flex h-5 w-9 shrink-0 items-center rounded-full transition-colors"
                                                        style={{ background: user.isActive === true ? "#22c55e" : "var(--surface-3)", border: `1px solid ${user.isActive === true ? "#22c55e" : "var(--border-medium)"}` }}
                                                    >
                                                        <span
                                                            className="h-3.5 w-3.5 rounded-full bg-white shadow-sm transition-transform"
                                                            style={{ transform: user.isActive === true ? "translateX(17px)" : "translateX(2px)" }}
                                                        />
                                                    </span>
                                                    <span style={{ color: updatingUserId === user.userId ? "var(--text-faint)" : user.isActive === true ? "#4ade80" : "var(--text-secondary)" }}>
                                                        {updatingUserId === user.userId ? "Updating..." : user.isActive === true ? "Active" : "Inactive"}
                                                    </span>
                                                </button>
                                                </div>
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </table>
                        </div>
                    </section>
                </div>
            </main>
            {isFormOpen && (
                <div
                    className="fixed inset-0 z-50 flex items-center justify-center p-4"
                    style={{ background: "rgba(2,6,23,0.72)" }}
                    onMouseDown={() => !isSavingUser && setIsFormOpen(false)}
                >
                    <form
                        onSubmit={handleSaveUser}
                        onMouseDown={(event) => event.stopPropagation()}
                        className="max-h-[90vh] w-full max-w-xl overflow-y-auto rounded-xl p-5 sm:p-6"
                        style={{ background: "var(--surface-1)", border: "1px solid var(--border-medium)" }}
                        aria-labelledby="user-form-title"
                    >
                        <div className="mb-5 flex items-start justify-between gap-4">
                            <div>
                                <h2 id="user-form-title" className="text-base font-bold" style={{ color: "var(--text-primary)" }}>
                                    {editingUser ? "Edit user" : "Add user"}
                                </h2>
                                <p className="mt-1 text-[11px]" style={{ color: "var(--text-faint)" }}>
                                    {editingUser ? `Update ${editingUser.username}'s account details.` : "Create an account and assign one or more roles."}
                                </p>
                            </div>
                            <button
                                type="button"
                                onClick={() => setIsFormOpen(false)}
                                disabled={isSavingUser}
                                aria-label="Close user form"
                                className="flex h-8 w-8 items-center justify-center rounded-lg disabled:opacity-50"
                                style={{ background: "var(--surface-3)", color: "var(--text-secondary)" }}
                            >
                                <Icon d={IC.x} size={15} />
                            </button>
                        </div>

                        {actionError && (
                            <div className="mb-4 rounded-lg px-3 py-2.5 text-[12px]" role="alert" style={{ background: "rgba(239,68,68,0.1)", border: "1px solid rgba(239,68,68,0.25)", color: "#f87171" }}>
                                {actionError}
                            </div>
                        )}

                        <div className="grid gap-4 sm:grid-cols-2">
                            <label className="space-y-1.5">
                                <span className="text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Full name</span>
                                <input required value={userForm.fullName} onChange={(event) => setUserForm({ ...userForm, fullName: event.target.value })} className="w-full rounded-lg px-3 py-2.5 text-[12px] outline-none" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }} />
                            </label>
                            <label className="space-y-1.5">
                                <span className="text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Username</span>
                                <input required autoComplete="username" value={userForm.username} onChange={(event) => setUserForm({ ...userForm, username: event.target.value })} className="w-full rounded-lg px-3 py-2.5 text-[12px] outline-none" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }} />
                            </label>
                            <label className="space-y-1.5">
                                <span className="text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Email</span>
                                <input required type="email" autoComplete="email" value={userForm.email} onChange={(event) => setUserForm({ ...userForm, email: event.target.value })} className="w-full rounded-lg px-3 py-2.5 text-[12px] outline-none" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }} />
                            </label>
                            <label className="space-y-1.5">
                                <span className="text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Phone number</span>
                                <input type="tel" autoComplete="tel" value={userForm.phoneNumber} onChange={(event) => setUserForm({ ...userForm, phoneNumber: event.target.value })} className="w-full rounded-lg px-3 py-2.5 text-[12px] outline-none" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }} />
                            </label>
                            {!editingUser && (
                                <label className="space-y-1.5 sm:col-span-2">
                                    <span className="text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Initial password</span>
                                    <input required type="password" autoComplete="new-password" value={userForm.password} onChange={(event) => setUserForm({ ...userForm, password: event.target.value })} className="w-full rounded-lg px-3 py-2.5 text-[12px] outline-none" style={{ background: "var(--surface-3)", border: "1px solid var(--border-medium)", color: "var(--text-primary)" }} />
                                </label>
                            )}
                            <fieldset className="space-y-2 sm:col-span-2">
                                <legend className="mb-2 text-[11px] font-medium" style={{ color: "var(--text-secondary)" }}>Roles</legend>
                                <div className="grid gap-2 sm:grid-cols-3">
                                    {displayedRoleOptions.map((role) => (
                                        <label key={role} className="flex items-center gap-2 rounded-lg px-3 py-2.5 text-[11px]" style={{ background: "var(--surface-3)", border: "1px solid var(--border-subtle)", color: "var(--text-primary)" }}>
                                            <input type="checkbox" checked={userForm.roles.includes(role)} onChange={() => toggleFormRole(role)} className="accent-cyan-400" />
                                            <span>{role}</span>
                                        </label>
                                    ))}
                                </div>
                            </fieldset>
                        </div>

                        <div className="mt-6 flex justify-end gap-2 border-t pt-4" style={{ borderColor: "var(--border-subtle)" }}>
                            <button type="button" onClick={() => setIsFormOpen(false)} disabled={isSavingUser} className="rounded-lg px-3.5 py-2 text-[12px] font-semibold disabled:opacity-50" style={{ background: "var(--surface-3)", color: "var(--text-secondary)" }}>
                                Cancel
                            </button>
                            <button type="submit" disabled={isSavingUser} className="inline-flex items-center gap-2 rounded-lg px-4 py-2 text-[12px] font-bold disabled:opacity-50" style={{ background: "#22d3ee", color: "#07111b" }}>
                                {isSavingUser && <Icon d={IC.refresh} size={13} />}
                                {isSavingUser ? "Saving..." : editingUser ? "Save changes" : "Create user"}
                            </button>
                        </div>
                    </form>
                </div>
            )}
        </div>
    );
}