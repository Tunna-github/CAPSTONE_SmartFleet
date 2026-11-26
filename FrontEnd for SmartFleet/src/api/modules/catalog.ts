import { useCallback, useEffect, useState } from "react";
import { apiClient } from "../client";
export interface CatalogWarehouse { warehouseId:number; code:string; name:string; address?:string; isActive:boolean; revision:number }
export interface CatalogZone { zoneId:number; warehouseId:number; code:string; name:string; type:string; speedLimit:number; isActive:boolean; revision:number }
export interface CatalogStation { stationId:number; zoneId:number; warehouseId:number; code:string; name:string; type:string; coordX:number; coordY:number; coordZ:number; yawAngle:number; qrCodePayload:string|null; arucoId:number|null; isActive:boolean; capacity:number; revision:number; usable:boolean; isOccupied:boolean; occupantRobotCodes:string[]; reservedRobotId:number|null }
export interface Catalog { warehouses:CatalogWarehouse[]; zones:CatalogZone[]; stations:CatalogStation[]; robots:{robotId:number;robotCode:string;warehouseId:number}[]; navigationWarehouseId:number; mapRevision:number; capacityPolicy:string }
export function useCatalog() {
    const [catalog,setCatalog]=useState<Catalog|null>(null);
    const [error,setError]=useState("");
    const reload=useCallback(async()=>{
        try { const {data}=await apiClient.get<Catalog>("/api/v1/warehouse-catalog");
            if (!Array.isArray(data.warehouses)||!Array.isArray(data.zones)||!Array.isArray(data.stations)) throw new Error("Station catalog unavailable");
            setCatalog(data);setError("");
        } catch(e) {setError((e as {message?:string}).message||"Cannot load station catalog");}
    },[]);
    useEffect(()=>{void reload();},[reload]);
    return {catalog,error,reload};
}
