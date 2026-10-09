import { apiClient } from "../client";
import { ENDPOINTS } from "../endpoints";
import type { ApiResponse, AuthUser } from "../types";

type CurrentUserResponse = AuthUser | ApiResponse<AuthUser>;
type UsersListResponse = AuthUser[] | ApiResponse<AuthUser[]>;

export interface CreateUserPayload {
    fullName: string;
    username: string;
    email: string;
    phoneNumber?: string;
    password: string;
    roles: string[];
}

export type UpdateUserPayload = Omit<CreateUserPayload, "password"> & {
    isActive: boolean;
};

export async function getMyProfile(): Promise<AuthUser> {
    const { data } = await apiClient.get<CurrentUserResponse>(ENDPOINTS.users.me);
    return "data" in data ? data.data : data;
}

export async function getUsers(): Promise<AuthUser[]> {
    const { data } = await apiClient.get<UsersListResponse>(ENDPOINTS.users.list);
    return Array.isArray(data) ? data : data.data;
}

export async function activateUser(userId: number): Promise<void> {
    await apiClient.patch(ENDPOINTS.users.activate(userId));
}

export async function deactivateUser(userId: number): Promise<void> {
    await apiClient.patch(ENDPOINTS.users.deactivate(userId));
}

export async function createUser(payload: CreateUserPayload): Promise<void> {
    await apiClient.post(ENDPOINTS.users.list, payload);
}

export async function updateUser(userId: number, payload: UpdateUserPayload): Promise<void> {
    await apiClient.put(ENDPOINTS.users.update(userId), payload);
}