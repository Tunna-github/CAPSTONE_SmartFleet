import { createContext, useContext, useEffect, useState, ReactNode, useCallback } from "react";
import { Task, LogEntry } from "../data/mockData";
import { getTransportTasks, createTransportTask, deleteTransportTask, type CreateTransportTaskPayload } from "../api/modules/tasks";
import { useAuth } from "./AuthContext";
import { intervene } from "../api/modules/interventions";

interface DataContextType {
    tasks: Task[];
    logs: LogEntry[];
    isTasksLoading: boolean;
    tasksError: string | null;
    addTask: (payload: CreateTransportTaskPayload) => Promise<Task>;
    setTaskRobot: (taskId: number | string, robotDisplay: string) => void;
    deleteTask: (task: Task) => Promise<void>;
    refreshTasks: (showErrorToast?: boolean) => Promise<void>;
    cancelTask: (id: string, reason: string) => Promise<void>;
    addLog: (log: Omit<LogEntry, "id" | "time">) => void;
}

const DataContext = createContext<DataContextType | undefined>(undefined);

export function DataProvider({ children }: { children: ReactNode }) {
    const { user, role, isAuthenticated, isLoading: isAuthLoading } = useAuth();
    const canLoadTasks = !isAuthLoading && isAuthenticated && user?.roles?.includes("Warehouse Operator") === true;
    const [tasks, setTasks] = useState<Task[]>([]);
    const [logs, setLogs] = useState<LogEntry[]>([]);
    const [isTasksLoading, setIsTasksLoading] = useState(false);
    const [tasksError, setTasksError] = useState<string | null>(null);

    const addLog = useCallback((log: Omit<LogEntry, "id" | "time">) => {
        setLogs(prev => [{
            ...log,
            id: Date.now(),
            time: new Date().toLocaleTimeString("en-GB", { hour12: false })
        }, ...prev].slice(0, 50));
    }, []);

    const refreshTasks = useCallback(async (showErrorToast = false) => {
        if (!canLoadTasks) return;
        setIsTasksLoading(true);
        setTasksError(null);

        try {
            const apiTasks = await getTransportTasks();
            setTasks(apiTasks);
        } catch (error) {
            const message = error instanceof Error ? error.message : "Failed to load tasks from API.";
            setTasksError(message);

            if (showErrorToast) {
                addLog({ level: "ERROR", robot: "SYSTEM", message });
            }

            throw error;
        } finally {
            setIsTasksLoading(false);
        }
    }, [addLog, canLoadTasks, user?.userId]);

    useEffect(() => {
        if (!canLoadTasks) {
            setTasks([]);
            setTasksError(null);
            setIsTasksLoading(false);
            return;
        }
        let isMounted = true;

        async function loadTasks() {
            try {
                const apiTasks = await getTransportTasks();

                if (isMounted) {
                    setTasks(apiTasks);
                    setTasksError(null);
                }
            } catch (error) {
                if (isMounted) {
                    const message = error instanceof Error ? error.message : "Failed to load tasks from API.";
                    setTasksError(message);
                }
            } finally {
                if (isMounted) {
                    setIsTasksLoading(false);
                }
            }
        }

        setIsTasksLoading(true);
        loadTasks();

        return () => {
            isMounted = false;
        };
    }, [canLoadTasks, user?.userId]);

    const addTask = async (payload: CreateTransportTaskPayload): Promise<Task> => {
        const newTask = await createTransportTask(payload);
        setTasks(prev => [newTask, ...prev.filter(task => task.id !== newTask.id)]);
        addLog({ level: "INFO", robot: "SYSTEM", message: `Task ${newTask.id} created successfully.` });
        return newTask;
    };

    const setTaskRobot = (taskId: number | string, robotDisplay: string) => {
        setTasks(prev => prev.map(task => task.taskId === taskId
            ? { ...task, robotId: robotDisplay, status: task.status === "QUEUED" ? "ASSIGNED" : task.status }
            : task));
    };

    const deleteTask = async (task: Task): Promise<void> => {
        if (task.taskId === undefined || task.taskId === null || task.taskId === "") {
            throw new Error("This task has no backend ID. Reload the task list before deleting.");
        }
        await deleteTransportTask(task.taskId);
        setTasks(prev => prev.filter(item => item.taskId !== task.taskId));
        addLog({ level: "INFO", robot: "SYSTEM", message: `Task ${task.id} deleted successfully.` });
    };

    const cancelTask = async (id: string, reason: string) => {
        const task = tasks.find(t => t.id === id);
        if (!task?.taskId) throw new Error("Task has no persisted backend ID.");
        await intervene(Number(task.taskId), "cancel", reason);
        await refreshTasks();
        addLog({ level: "WARN", robot: "SYSTEM", message: `Cancellation saved for ${id}; robot release requires safety confirmation.` });
    };

    return (
        <DataContext.Provider
            value={{
                tasks,
                logs,
                isTasksLoading,
                tasksError,
                addTask,
                setTaskRobot,
                deleteTask,
                refreshTasks,
                cancelTask,
                addLog,
            }}
        >
            {children}
        </DataContext.Provider>
    );
}

export function useData() {
    const context = useContext(DataContext);
    if (!context) throw new Error("useData must be used within DataProvider");
    return context;
}
