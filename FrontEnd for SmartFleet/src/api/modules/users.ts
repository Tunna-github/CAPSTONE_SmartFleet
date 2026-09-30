import { apiClient } from "../client";
import { ENDPOINTS } from "../endpoints";
import type { ApiResponse, AuthUser } from "../types";

type CurrentUserResponse = AuthUser | ApiResponse<AuthUser>;

export async function getMyProfile(): Promise<AuthUser> {
    const { data } = await apiClient.get<CurrentUserResponse>(ENDPOINTS.users.me);
    return "data" in data ? data.data : data;
}