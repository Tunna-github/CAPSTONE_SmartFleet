import axios, {
    AxiosError,
    AxiosInstance,
    InternalAxiosRequestConfig,
} from "axios";
import type { ApiError } from "./types";

// Same-origin gateway works on localhost, LAN hosts and deployed HTTPS domains.
export const apiBaseUrl = import.meta.env.VITE_API_URL ?? "";

// ─── Token storage ───────────────────────────────────────────────────────────
export const tokenStorage = {
    get: () => localStorage.getItem("smartfleet_access_token"),
    set: (t: string) => localStorage.setItem("smartfleet_access_token", t),
    getRefresh: () => localStorage.getItem("smartfleet_refresh_token"),
    setRefresh: (t: string) => localStorage.setItem("smartfleet_refresh_token", t),
    getUser: () => {
        const raw = localStorage.getItem("smartfleet_user");
        if (!raw) return null;
        try {
            const value = JSON.parse(raw);
            if (!value || typeof value.userId !== "number" || !Array.isArray(value.roles) ||
                value.roles.some((role: unknown) => typeof role !== "string")) return null;
            return value;
        } catch { return null; }
    },
    setUser: (u: unknown) =>
        localStorage.setItem("smartfleet_user", JSON.stringify(u)),
    clear: () => {
        localStorage.removeItem("smartfleet_access_token");
        localStorage.removeItem("smartfleet_refresh_token");
        localStorage.removeItem("smartfleet_user");
    },
};

// ─── Axios instance ──────────────────────────────────────────────────────────
export const apiClient: AxiosInstance = axios.create({
    baseURL: apiBaseUrl,
    timeout: 15000,
    headers: {
        "Content-Type": "application/json",
        Accept: "application/json",
    },
});

// ─── Request interceptor: attach Bearer token ────────────────────────────────
apiClient.interceptors.request.use(
    (config: InternalAxiosRequestConfig) => {
        const token = tokenStorage.get();
        if (token && config.headers) {
            config.headers.Authorization = `Bearer ${token}`;
        }
        return config;
    },
    (error) => Promise.reject(error)
);

// ─── Response interceptor: normalize errors + handle 401 ─────────────────────
apiClient.interceptors.response.use(
    (response) => response,
    (error: AxiosError) => {
        const status = error.response?.status ?? 0;

        if (status === 401) {
            tokenStorage.clear();
            if (!window.location.pathname.startsWith("/login")) {
                window.location.href = "/login";
            }
        }

        const data: any = error.response?.data;
        const normalized: ApiError = {
            status,
            message:
                data?.message ||
                data?.detail ||
                data?.title ||
                data?.error ||
                (typeof data?.result === "string" ? data.result : null) ||
                error.message ||
                "Something went wrong",
            errors: data?.errors,
        };

        return Promise.reject(normalized);
    }
);
