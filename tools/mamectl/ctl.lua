-- Remote control for MAME, used to set options in games' service menus.
-- Every frame it looks for cmd.txt next to this script, runs its lines, then deletes it.
--   list                    write all input fields to fields.txt
--   press <frames> <name>   hold an input (exact field name) for N frames
--   set <value> <name>      force a field (e.g. a DIP switch) to a value
--   clear <name>            release a forced field
--   wait <frames>           pause the command queue
--   snap                    save a snapshot (snap/<game>/NNNN.png)
--   exit                    quit MAME normally (NVRAM/EEPROM is saved)
local dir = os.getenv("LIGHTGUN_CTL")
local cmdfile, logfile = dir .. "/cmd.txt", dir .. "/ctl.log"
local queue, holds, waitframes = {}, {}, 0

local function log(s)
  local f = io.open(logfile, "a"); if f then f:write(s .. "\n"); f:close() end
end

local function field(name)
  -- "PORTTAG|Field name" picks a field in one port when names repeat.
  local wanttag, wantname = name:match("^(.-)|(.+)$")
  for tag, port in pairs(manager.machine.ioport.ports) do
    for fname, f in pairs(port.fields) do
      if wanttag then
        if tag == wanttag and fname == wantname then return f end
      elseif fname == name then return f end
    end
  end
  log("no field: " .. name)
end

local function run(line)
  local cmd, rest = line:match("^(%S+)%s*(.*)$")
  if not cmd then return end
  if cmd == "list" then
    local f = io.open(dir .. "/fields.txt", "w")
    for tag, port in pairs(manager.machine.ioport.ports) do
      for fname, fld in pairs(port.fields) do
        f:write(string.format("%s | %s | def=%s\n", tag, fname, tostring(fld.defvalue)))
      end
    end
    f:close(); log("listed")
  elseif cmd == "press" then
    local n, name = rest:match("^(%d+)%s+(.+)$")
    local fld = field(name)
    if fld then fld:set_value(1); holds[#holds + 1] = { f = fld, n = tonumber(n) }; log("press " .. name) end
  elseif cmd == "set" then
    local v, name = rest:match("^(%d+)%s+(.+)$")
    local fld = field(name); if fld then fld:set_value(tonumber(v)); log("set " .. name .. "=" .. v) end
  elseif cmd == "clear" then
    local fld = field(rest); if fld then fld:clear_value(); log("clear " .. rest) end
  elseif cmd == "wait" then
    waitframes = tonumber(rest) or 0
  elseif cmd == "snap" then
    manager.machine.video:snapshot(); log("snap")
  elseif cmd == "done" then
    log("done " .. rest)
  elseif cmd == "exit" then
    log("exit"); manager.machine:exit()
  end
end

_G.lightgun_ctl_sub = emu.add_machine_frame_notifier(function()
  for i = #holds, 1, -1 do
    local h = holds[i]; h.n = h.n - 1
    if h.n <= 0 then h.f:clear_value(); table.remove(holds, i) end
  end
  if waitframes > 0 then waitframes = waitframes - 1; return end
  if #queue == 0 then
    local f = io.open(cmdfile, "r")
    if f then
      for line in f:lines() do queue[#queue + 1] = line end
      f:close(); os.remove(cmdfile)
    end
  end
  while #queue > 0 and waitframes == 0 do
    run(table.remove(queue, 1))
  end
end)
log("ctl ready: " .. manager.machine.system.name)
