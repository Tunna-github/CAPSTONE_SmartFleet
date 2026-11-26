-- New isolated simulation only; never imports historical tasks or outbound commands.
BEGIN;
SET search_path TO public;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
\getenv admin_password DEMO_ADMIN_PASSWORD
\getenv operator_password DEMO_OPERATOR_PASSWORD
\getenv maintenance_password DEMO_MAINTENANCE_PASSWORD
\set map_json `cat /simulation/config/map.json`
INSERT INTO public."Warehouses" ("WarehouseCode","WarehouseName") VALUES ('SIM','SmartFleet simulation');
INSERT INTO public."Zones" ("WarehouseID","ZoneCode","ZoneName","ZoneType") VALUES (1,'SIM','Simulation floor','TRANSIT');
INSERT INTO public."Stations" ("ZoneID","StationCode","StationName","StationType","CoordX","CoordY","QrCodePayload")
VALUES (1,'P1','Pickup Station P1','PICKUP',4,5,'P1'),(1,'D1','Delivery Station D1','DELIVERY',6,1,'D1');
-- Explicit commissioning, independent of numeric StationID values.
INSERT INTO public."StationCheckpoints" ("StationId","WarehouseId","ArucoId")
SELECT s."StationID",z."WarehouseID",CASE s."StationCode" WHEN 'P1' THEN 2 ELSE 7 END
FROM public."Stations" s JOIN public."Zones" z ON z."ZoneID"=s."ZoneID"
JOIN public."Warehouses" w ON w."WarehouseID"=z."WarehouseID"
WHERE w."WarehouseCode"='SIM' AND s."StationCode" IN ('P1','D1');
INSERT INTO public."Roles" ("RoleName") VALUES ('Administrator'),('Warehouse Operator'),('Maintenance Technician');
INSERT INTO public."Users" ("Username","Email","PasswordHash","FullName") VALUES
('admin','admin@smartfleet.local',crypt(:'admin_password',gen_salt('bf',12)),'Simulation Administrator'),
('operator','operator@smartfleet.local',crypt(:'operator_password',gen_salt('bf',12)),'Simulation Operator'),
('maintenance','maintenance@smartfleet.local',crypt(:'maintenance_password',gen_salt('bf',12)),'Simulation Maintenance');
INSERT INTO public."UserRoles" ("UserID","RoleID") VALUES (1,1),(2,2),(3,3);
INSERT INTO public."Robots" ("WarehouseID","RobotCode","RobotType","Model","ControlSoftware","MqttClientIdentifier","MaxPayloadWeightKg")
VALUES (1,'R01','SIMULATION','TurtleBot3','WEBOTS_ARUCO_IMU','smartfleet-R01',10),(1,'R02','SIMULATION','TurtleBot3','WEBOTS_ARUCO_IMU','smartfleet-R02',10);
INSERT INTO public."MapConfigurations" ("WarehouseID","MapVersion","MapName","MapSource","MapMetadataJson")
VALUES (1,'simulation-v1','Commissioned Webots floor','MANUAL',jsonb_build_object('navigationMap',:'map_json'::jsonb)::text);
CREATE TABLE public.smartfleet_demo_bootstrap(version integer PRIMARY KEY);
INSERT INTO public.smartfleet_demo_bootstrap VALUES (1);
COMMIT;
