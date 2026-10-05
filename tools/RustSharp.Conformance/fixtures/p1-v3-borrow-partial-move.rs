// frozen P1 fixture: borrow-partial-move
struct Leaf { value: i32 }
struct Pair { left: Leaf, right: i32 }
fn main() {
    let pair = Pair { left: Leaf { value: 1 }, right: 2 };
    let moved = pair.left;
    println!("{}", moved.value);
    println!("{}", pair.right);
}
