const BASE = "/api/v1";

export const ENDPOINTS = {
    auth: {
        login: `${BASE}/auth/login`,
        // logout: `${BASE}/auth/logout`,
        // refresh: `${BASE}/auth/refresh`,
        // me: `${BASE}/auth/me`,
    },
    users: {
        list: `${BASE}/users`,
        me: `${BASE}/users/me`,
        update: (userId: number) => `${BASE}/users/${userId}`,
        activate: (userId: number) => `${BASE}/users/${userId}/activate`,
        deactivate: (userId: number) => `${BASE}/users/${userId}/deactivate`,
    },
    transportTasks: {
        list: `${BASE}/transport-tasks`,
        create: `${BASE}/transport-tasks`,
        delete: (taskId: number | string) => `${BASE}/transport-tasks/${encodeURIComponent(taskId)}`,
        detail: (taskId: number | string) => `${BASE}/transport-tasks/${taskId}`,
        availableRobots: (taskId: number | string) => `${BASE}/transport-tasks/${taskId}/available-robots`,
    },
    accessTest: {
        authenticated: `${BASE}/access-test/authenticated`,
        admin: `${BASE}/access-test/admin`,
        operator: `${BASE}/access-test/operator`,
        maintenance: `${BASE}/access-test/maintenance`,
    },
    maintenanceRecords: {
        list: `${BASE}/maintenance-records`,
    },
    robots: {
        testMove: (robotId: string) => `/api/robots/${robotId}/test-move`,
    },
    sessions: {
        create: `${BASE}/sessions`,
    },
    test: {
        ping: `/api/Test`,
    },
};
