// ─── Shared API types ────────────────────────────────────────────────────────

export interface ApiError {
    message: string;
    status: number;
    errors?: Record<string, string[]>;
}

export interface ApiResponse<T> {
    success: boolean;
    data: T;
    message?: string;
}

// ─── Auth ────────────────────────────────────────────────────────────────────

export interface LoginRequest {
    usernameOrEmail: string;
    password: string;
}

export interface AuthUser {
    userId: number;
    username: string;
    email: string;
    fullName: string;
    phoneNumber?: string;
    isActive?: boolean;
    roles: string[];      // e.g. ["Warehouse Operator"]
}

export interface LoginResponse {
    accessToken: string;
    refreshToken?: string;
    expiresIn?: number;
    user: AuthUser;
}