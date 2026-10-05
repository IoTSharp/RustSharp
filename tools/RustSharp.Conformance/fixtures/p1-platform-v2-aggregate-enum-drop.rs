// P1 platform: an enum aggregate is selected and consumed by a pattern.
enum Choice { One(i32), Two(i32) }
fn main() {
    let choice = Choice::One(7);
    match choice {
        Choice::One(_) => println!("one"),
        Choice::Two(_) => println!("two"),
    }
}
