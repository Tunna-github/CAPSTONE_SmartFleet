import { apiClient, tokenStorage } from "../client";
import { ENDPOINTS } from "../endpoints";
import type { LoginRequest, LoginResponse, AuthUser } from "../types";
import { getMyProfile } from "./users";

/**
 * POST /api/v1/auth/login
 */
export async function login(payload: LoginRequest): Promise<LoginResponse> {
    const { data } = await apiClient.post<LoginResponse>(
        ENDPOINTS.auth.login,
        payload
    );

    if (data.accessToken) tokenStorage.set(data.accessToken);
    if (data.refreshToken) tokenStorage.setRefresh(data.refreshToken);
    if (data.user) tokenStorage.setUser(data.user);

    return data;
}

/**
 * Clears local session. Calls backend logout if available.
 */
export async function logout(): Promise<void> {
    try {
        // await apiClient.post(ENDPOINTS.auth.logout);
    } catch {
        // ignore
    } finally {
        tokenStorage.clear();
    }
}

export function getCurrentUser(): AuthUser | null {
    return tokenStorage.getUser();
}

export function saveCurrentUser(user: AuthUser): void {
    tokenStorage.setUser(user);
}

export function isAuthenticated(): boolean {
    return !!tokenStorage.get();
}

export async function validateCurrentSession(): Promise<AuthUser | null> {
    const token = tokenStorage.get();

    if (!token) {
        tokenStorage.clear();
        return null;
    }

    try {
        const currentUser = await getMyProfile();
        tokenStorage.setUser(currentUser);
        return currentUser;
    } catch {
        tokenStorage.clear();
        return null;
    }
}
