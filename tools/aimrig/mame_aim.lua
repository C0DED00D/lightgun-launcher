-- Logs the lightgun X/Y input values (and their ranges) to the file named by env AIM_OUT, rewritten twice a second.
local out = os.getenv("AIM_OUT") or "mame_aim.txt"
local frame = 0
local function shift_of(mask)
	local s = 0
	while mask ~= 0 and (mask & 1) == 0 do mask = mask >> 1; s = s + 1 end
	return s
end
-- Keep the subscription in a global, otherwise Lua garbage-collects it and the callback stops.
aim_sub = emu.add_machine_frame_notifier(function()
	frame = frame + 1
	if frame % 30 ~= 0 then return end
	local lines = {}
	for tag, port in pairs(manager.machine.ioport.ports) do
		for name, field in pairs(port.fields) do
			if field.type_class == "controller" and (string.find(name, "Lightgun") or string.find(name, "Gun")) then
				local v = (port:read() & field.mask) >> shift_of(field.mask)
				lines[#lines + 1] = string.format("%s|%s|%d|%d|%d", tag, name, v, field.minvalue or -1, field.maxvalue or -1)
			end
		end
	end
	table.sort(lines)
	local f = io.open(out, "w")
	if f then f:write(table.concat(lines, "\n") .. "\n"); f:close() end
end)
