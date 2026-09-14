write a project-progress doc follow what instructed belows also refer to the structure and content of the .claude folder
also in the part that listed the current strucured of the .claude folder detailed the reason and why those are in use or
needed for the porject.
also put the Where the .claude tooling stands today on top before progress steps.

1. deciding what the project is: Warehouse Management System (WMS) intended to run real commercial warehouse operations
2. lock in the project core tech stach: The project is decided to be a .Net backend project along with react frontend
   with postgresql database.
3. create the initial .claude folder structure: write the initial .claude folder structure The initial .claude folder
   structure still have full contents of it right now. but for the intital version only the skills folder will have
   contents but only system-design.
4. Lauch claude code and start the project by inputing idea of what the project is so system-design skill will be able
   to generate the project proposal and design using the input back and forth between claude-code and user. The claude
   code will output wms-project-proposal.md file.
5. Review the project proposal and make changes.
6. After locking in the project proposal, promt claude-code to create a architechture review of the project by
   system-design skill using "act as a senior system architect engineer, confirm and review the proposal architecture".
   This will output the wms-architecture-review.md. in this step claude-code will also as for itput from user on the
   scope and features of the project.
7. Review the architecture review and make changes if needed.
8. After locking in the architecture, promt claude-code to create a detailed system-design of the project by
   system-design skill using "act as a senior system architect engineer, create a detailed system design of the
   project". This will output the wms-system-design.md.
9. Review the system design and make changes if needed.
10. After locking in the system detailed design, promt claude-code to add and or created needed skills, agents,
    commands, hooks and plugins needed for the project to pre-load all skill needed for the project and create CLAUDE.md
    file. explain more details of what created in this step and what created them.
11. Started scaffolding the project with the initial structures and phase 1A backend of the project. list down what was
    created.
12. Started scaffolding the frontend of the project just the basic structure of the frontend so it could be used to sync
    to claude-design to desing UI for the project then sync back to claude-code once it is done.
13. Using claude-code to create documentation of screens needed for the project so that it could be used to send to
    claude design to start designing UI. This out out wms-screen-inventory.md. file
14. Input the wms-screen-inventory.md and let claude-design to start designing UI starting from the admin screens.
