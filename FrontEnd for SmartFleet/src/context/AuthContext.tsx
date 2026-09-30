import {
    createContext,
    useContext,
    useCallback,
    useState,
    ReactNode,
} from "react";
import * as authApi from "../api/modules/auth";
import type { AuthUser } from "../api/types";

export type Role = "admin" | "operator" | "maintenance";

interface AuthContextType {
    user: AuthUser | null;
    role: Role | null;
    isAuthenticated: boolean;
    isLoading: boolean;
    login: (usernameOrEmail: string, password: string) => Promise<void>;
    logout: () => Promise<void>;
    updateUser: (user: AuthUser) => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

/**
 * Backend sends display-name roles like:
 *   "Warehouse Operator"
 *   "Maintenance Technician"
 *   "Administrator"
 * We map them to frontend role keys.
 */
function pickRole(roles: string[] | undefined): Role {
    if (!roles || roles.length === 0) return "operator";
    const normalized = roles.map((r) => r.toLowerCase().trim());

    if (normalized.some((r) => r.includes("admin") || r.includes("administrator"))) {
        return "admin";
    }
    if (normalized.some((r) => r.includes("maintenance") || r.includes("technician"))) {
        return "maintenance";
    }
    if (normalized.some((r) => r.includes("operator") || r.includes("warehouse"))) {
        return "operator";
    }
    return "operator";
}

export function AuthProvider({ children }: { children: ReactNode }) {
    const [user, setUser] = useState<AuthUser | null>(() => authApi.getCurrentUser());
    const [isLoading, setIsLoading] = useState(false);

    const role: Role | null = user ? pickRole(user.roles) : null;
    const isAuthenticated = !!user;

    async function login(usernameOrEmail: string, password: string) {
        setIsLoading(true);
        try {
            const res = await authApi.login({ usernameOrEmail, password });
            setUser(res.user);
        } finally {
            setIsLoading(false);
        }
    }

    async function logout() {
        await authApi.logout();
        setUser(null);
    }

    const updateUser = useCallback((nextUser: AuthUser) => {
        authApi.saveCurrentUser(nextUser);
        setUser(nextUser);
    }, []);

    return (
        <AuthContext.Provider
            value={{ user, role, isAuthenticated, isLoading, login, logout, updateUser }}
        >
            {children}
        </AuthContext.Provider>
    );
}

export function useAuth() {
    const ctx = useContext(AuthContext);
    if (!ctx) throw new Error("useAuth must be used inside <AuthProvider>");
    return ctx;
}