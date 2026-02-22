#include <SDL3/SDL.h>
#include <backends/imgui_impl_sdl3.h>
#include <backends/imgui_impl_sdlrenderer3.h>
#include <flecs.h>
#include <imgui.h>

auto main() -> int
{
    SDL_Log("Initializing ImGui + Flecs + SDL3");

    // =========================================================================
    // 1. SDL3 Initialization
    // =========================================================================
    if (!SDL_Init(SDL_INIT_VIDEO | SDL_INIT_EVENTS)) {
        SDL_Log("Error: SDL_Init failed: %s", SDL_GetError());
        return -1;
    }

    // Enable native scaling and resizability for a modern window feel
    Uint32 window_flags = SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY;
    SDL_Window* window = SDL_CreateWindow("ImGui + Flecs + SDL3 Architecture", 1280, 720, window_flags);
    if (!window) {
        SDL_Log("Error: SDL_CreateWindow failed: %s", SDL_GetError());
        return -1;
    }

    // Create the SDL3 Renderer. Passing nullptr for the name automatically 
    // chooses the best available hardware-accelerated renderer.
    SDL_Renderer* renderer = SDL_CreateRenderer(window, nullptr);
    if (!renderer) {
        SDL_Log("Error: SDL_CreateRenderer failed: %s", SDL_GetError());
        return -1;
    }

    // =========================================================================
    // 2. ImGui & Flecs Initialization
    // =========================================================================
    flecs::world world;
    world.component<int>();

    IMGUI_CHECKVERSION();
    ImGui::CreateContext();
    
    // Configure ImGui standard settings
    ImGuiIO& io = ImGui::GetIO(); (void)io;
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard; // Enable Keyboard Controls
    io.ConfigFlags |= ImGuiConfigFlags_NavEnableGamepad;  // Enable Gamepad Controls

    ImGui::StyleColorsDark();

    // Initialize the ImGui backends for SDL3 and the SDL_Renderer
    ImGui_ImplSDL3_InitForSDLRenderer(window, renderer);
    ImGui_ImplSDLRenderer3_Init(renderer);

    // =========================================================================
    // 3. The Main Application Loop
    // =========================================================================
    bool done = false;
    while (!done) {
        // --- Event Polling ---
        SDL_Event event;
        while (SDL_PollEvent(&event)) {
            // Feed events to ImGui first
            ImGui_ImplSDL3_ProcessEvent(&event);
            
            // Check for termination events
            if (event.type == SDL_EVENT_QUIT) {
                done = true;
            }
            if (event.type == SDL_EVENT_WINDOW_CLOSE_REQUESTED && 
                event.window.windowID == SDL_GetWindowID(window)) {
                done = true;
            }
        }

        // --- ECS Update ---
        // Progress the Flecs world. You would typically pass your delta time here.
        world.progress();

        // --- ImGui Frame Setup ---
        ImGui_ImplSDLRenderer3_NewFrame();
        ImGui_ImplSDL3_NewFrame();
        ImGui::NewFrame();

        // --- ImGui UI Construction ---
        ImGui::Begin("System Monitor");
        ImGui::Text("Hello from SDL3 and Dear ImGui!");
        ImGui::Separator();
        ImGui::Text("Application average %.3f ms/frame (%.1f FPS)", 
                    1000.0f / io.Framerate, io.Framerate);
        
        if (ImGui::Button("Quit Application")) {
            done = true;
        }
        ImGui::End();

        // --- Rendering ---
        ImGui::Render();
        
        // Set the background clear color (a soft dark grey)
        SDL_SetRenderDrawColor(renderer, 
                              (Uint8)(0.20f * 255), 
                              (Uint8)(0.22f * 255), 
                              (Uint8)(0.24f * 255), 
                              SDL_ALPHA_OPAQUE);
        SDL_RenderClear(renderer);
        
        // Draw the ImGui data via the SDL_Renderer
        ImGui_ImplSDLRenderer3_RenderDrawData(ImGui::GetDrawData(), renderer);
        
        // Swap buffers to display the frame
        SDL_RenderPresent(renderer);
    }

    // =========================================================================
    // 4. Graceful Teardown
    // =========================================================================
    ImGui_ImplSDLRenderer3_Shutdown();
    ImGui_ImplSDL3_Shutdown();
    ImGui::DestroyContext();

    SDL_DestroyRenderer(renderer);
    SDL_DestroyWindow(window);
    SDL_Quit();

    return 0;
}